using System.Net.Http.Json;
using System.Text.Json;
using McpServer.Tools;

namespace McpServer.Api;

// Department search is SearchService's job: it already keeps one embedding per department, kept current by the
// events, and answers keyword, semantic and hybrid. Read as the caller, through the service's own API, like every
// other tool.
public sealed class SearchApiClient(HttpClient http)
{
    // The service caps a page at this.
    public const int MaxLimit = 20;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<DepartmentSearchResult>> SearchDepartmentsAsync(
        string query, int limit, CancellationToken ct)
    {
        using var response = await http.GetAsync(
            $"api/search?q={Uri.EscapeDataString(query)}&types=department&limit={Math.Clamp(limit, 1, MaxLimit)}", ct);
        if (!response.IsSuccessStatusCode)
        {
            throw ServiceApiException.From(response.StatusCode);
        }

        var envelope = await response.Content.ReadFromJsonAsync<Envelope>(Json, ct);
        if (envelope is null || envelope.IsError)
        {
            throw ServiceApiException.From(System.Net.HttpStatusCode.InternalServerError);
        }

        return (envelope.Result?.Results ?? [])
            .Select(r => new DepartmentSearchResult(r.Id, r.Title, r.Subtitle ?? string.Empty, r.Rank))
            .ToList();
    }

    private sealed record Envelope(SearchDto? Result, bool IsError);

    private sealed record SearchDto(List<HitDto> Results);

    private sealed record HitDto(Guid Id, string Title, string? Subtitle, double Rank);
}
