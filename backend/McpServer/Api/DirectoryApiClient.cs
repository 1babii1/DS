using System.Net.Http.Json;
using System.Text.Json;
using McpServer.Tools;

namespace McpServer.Api;

public sealed class DirectoryApiClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<DepartmentSearchResult>> SearchAsync(
        string query, int limit, CancellationToken ct)
    {
        var results = await GetEnvelopeAsync<List<SearchDto>>(
            $"api/departments/search?query={Uri.EscapeDataString(query)}&limit={limit}", ct);
        return (results ?? []).Select(r => new DepartmentSearchResult(r.Id, r.Name, r.Identifier, r.Score)).ToList();
    }

    // The API returns a bare page with no total, and its default page size is 20. A full page is the
    // only hint there may be more, so HasMore can be a false positive when the last page is exactly
    // full; it is never a false negative.
    private const int RootsPageSize = 20;

    public async Task<DepartmentTreeResult> RootsAsync(int page, CancellationToken ct)
    {
        var roots = await GetEnvelopeAsync<List<HierarchyDto>>(
            $"api/departments/roots?page={page}&size={RootsPageSize}", ct) ?? [];

        // Inactive roots are dropped after paging, so a page can hold fewer than 20 and still have a next one.
        var nodes = roots
            .Where(d => d.IsActive)
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .Select(ToNode)
            .ToList();
        return new DepartmentTreeResult(nodes, HasMore: roots.Count >= RootsPageSize);
    }

    // One call: the subtree query lives in DirectoryService, which owns the hierarchy, and is bounded
    // there. An unknown or deleted department is an empty tree, not an error.
    public async Task<DepartmentTreeResult> SubtreeAsync(Guid departmentId, CancellationToken ct)
    {
        var subtree = await GetEnvelopeAsync<SubtreeDto>($"api/departments/{departmentId}/subtree", ct);
        return new DepartmentTreeResult(
            (subtree?.Nodes ?? []).Select(ToNode).ToList(), HasMore: subtree?.Truncated ?? false);
    }

    private static DepartmentNode ToNode(HierarchyDto d) =>
        new(d.Id, d.Name, d.Identifier, (short)d.Depth, d.ParentId);

    private async Task<T?> GetEnvelopeAsync<T>(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw ServiceApiException.From(response.StatusCode);
        }

        var envelope = await response.Content.ReadFromJsonAsync<Envelope<T>>(Json, ct);
        if (envelope is null || envelope.IsError)
        {
            throw ServiceApiException.From(System.Net.HttpStatusCode.InternalServerError);
        }

        return envelope.Result;
    }

    private sealed record Envelope<T>(T? Result, bool IsError);

    private sealed record SearchDto(Guid Id, string Name, string Identifier, double Score);

    private sealed record HierarchyDto(Guid Id, Guid? ParentId, string Name, string Identifier, int Depth, bool IsActive);

    private sealed record SubtreeDto(List<HierarchyDto> Nodes, bool Truncated);
}
