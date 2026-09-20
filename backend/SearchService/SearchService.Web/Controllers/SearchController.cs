using CSharpFunctionalExtensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SearchService.Domain;
using SearchService.Infrastructure.Elasticsearch;
using Shared;
using Shared.EndpointResults;

namespace SearchService.Web.Controllers;

public record SearchResultDto(string Kind, Guid Id, string Title, string? Subtitle, string[] MatchedFields, double Rank);

public record SearchResponse(string Query, IReadOnlyList<SearchResultDto> Results);

// [Authorize] only - no finer-grained per-result authorization exists anywhere in this
// codebase today (every other list/get endpoint is endpoint-level-authorized only, no
// record-level ACL system). Matching that granularity here is a documented decision (see
// the search ADR), not an oversight.
[ApiController]
[Route("api/search")]
[Authorize]
public class SearchController(SearchIndexClient indexClient, ILogger<SearchController> logger) : ControllerBase
{
    private const int DefaultLimit = 8;
    private const int MaxLimit = 20;

    [HttpGet]
    public async Task<EndpointResult<SearchResponse>> Search(
        [FromQuery] string? q,
        [FromQuery] string? types,
        [FromQuery] int? limit,
        CancellationToken cancellationToken)
    {
        var query = q?.Trim() ?? string.Empty;
        if (query.Length is < 2 or > 100)
        {
            return Result.Failure<SearchResponse, Error>(
                Error.Validation("search.query.invalid_length", "Query must be 2-100 characters", "q"));
        }

        IReadOnlyCollection<string>? kinds = null;
        if (!string.IsNullOrWhiteSpace(types))
        {
            var requested = types.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var invalid = requested.Where(k => !SearchKind.IsValid(k)).ToArray();
            if (invalid.Length > 0)
            {
                return Result.Failure<SearchResponse, Error>(
                    Error.Validation(
                        "search.types.invalid",
                        $"Unknown type(s): {string.Join(", ", invalid)}",
                        "types"));
            }

            kinds = requested.Select(k => k.ToLowerInvariant()).ToArray();
        }

        var size = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

        IReadOnlyList<SearchHit> hits;
        try
        {
            hits = await indexClient.SearchAsync(query, kinds, size, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Search query failed for {Query}", query);
            return Result.Failure<SearchResponse, Error>(
                Error.Unavailable("search.index.unavailable", "Search is temporarily unavailable"));
        }

        var results = hits
            .Select(h => new SearchResultDto(h.Kind, h.SourceId, h.Title, h.Subtitle, h.MatchedFields, h.Rank))
            .ToList();

        return Result.Success<SearchResponse, Error>(new SearchResponse(query, results));
    }
}
