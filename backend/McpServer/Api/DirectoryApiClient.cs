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

    // The service answers an unknown id with a successful empty result, so null here means "no such department".
    public async Task<DepartmentInfo?> GetDepartmentAsync(Guid departmentId, CancellationToken ct)
    {
        var department = await GetEnvelopeAsync<DepartmentDto>($"api/departments/department/{departmentId}", ct);
        return department is null ? null : new DepartmentInfo(department.Id, department.Name, department.IsActive);
    }

    // The positions the department really has. Asking for these, rather than for one position by id, is what
    // ties a position to a department: a position of another department, or a department id passed as a
    // position, is simply not in the list.
    public async Task<IReadOnlyList<PositionInfo>> ActivePositionsOfAsync(Guid departmentId, CancellationToken ct)
    {
        var found = new List<PositionInfo>();
        for (var page = 1; page <= MaxPositionPages; page++)
        {
            using var response = await http.GetAsync(
                $"api/positions?departmentId={departmentId}&isActive=true&page={page}&size={PositionsPageSize}", ct);
            if (!response.IsSuccessStatusCode)
            {
                throw ServiceApiException.From(response.StatusCode);
            }

            var paged = await response.Content.ReadFromJsonAsync<PositionsPageDto>(Json, ct)
                ?? throw ServiceApiException.From(System.Net.HttpStatusCode.InternalServerError);
            found.AddRange(paged.Items.Select(p => new PositionInfo(p.Id, p.Name)));
            if (found.Count >= paged.Total || paged.Items.Count == 0)
            {
                break;
            }
        }

        return found;
    }

    private const int PositionsPageSize = 200;
    private const int MaxPositionPages = 5;

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

    private sealed record DepartmentDto(Guid Id, string Name, bool IsActive);

    private sealed record PositionDto(Guid Id, string Name);

    private sealed record PositionsPageDto(List<PositionDto> Items, int Total);

    private sealed record SubtreeDto(List<HierarchyDto> Nodes, bool Truncated);
}

public sealed record DepartmentInfo(Guid Id, string Name, bool IsActive);

public sealed record PositionInfo(Guid Id, string Name);
