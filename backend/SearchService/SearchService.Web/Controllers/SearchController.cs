using CSharpFunctionalExtensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SearchService.Domain;
using SearchService.Infrastructure.Elasticsearch;
using SearchService.Infrastructure.Postgres.Embeddings;
using Shared;
using Shared.EndpointResults;

namespace SearchService.Web.Controllers;

public record SearchResultDto(string Kind, Guid Id, string Title, string? Subtitle, string[] MatchedFields, double Rank);

// Mode is what actually answered: a hybrid request that could not reach the semantic side answers as "keyword".
public record SearchResponse(string Query, IReadOnlyList<SearchResultDto> Results, string Mode = "keyword");

public static class SearchModes
{
    public const string Keyword = "keyword";
    public const string Semantic = "semantic";
    public const string Hybrid = "hybrid";
}

// [Authorize] only - no finer-grained per-result authorization exists anywhere in this
// codebase today (every other list/get endpoint is endpoint-level-authorized only, no
// record-level ACL system). Matching that granularity here is a documented decision (see
// the search ADR), not an oversight.
[ApiController]
[Route("api/search")]
[Authorize]
public class SearchController(
    SearchIndexClient indexClient, SemanticSearch semanticSearch, ILogger<SearchController> logger) : ControllerBase
{
    private const int DefaultLimit = 8;
    private const int MaxLimit = 20;

    // How many candidates each source contributes to a fusion. Deep enough that an item ranked 30th by one source
    // and 2nd by the other is still seen.
    private const int CandidateDepth = 50;

    // A hybrid search must not become slower than the keyword one it improves on: if the model does not answer in
    // this time, the answer is the keyword results.
    private static readonly TimeSpan SemanticBudget = TimeSpan.FromSeconds(3);

    [HttpGet]
    public async Task<EndpointResult<SearchResponse>> Search(
        [FromQuery] string? q,
        [FromQuery] string? types,
        [FromQuery] int? limit,
        [FromQuery] string? mode,
        CancellationToken cancellationToken)
    {
        var requestedMode = string.IsNullOrWhiteSpace(mode) ? SearchModes.Hybrid : mode.Trim().ToLowerInvariant();
        if (requestedMode is not (SearchModes.Keyword or SearchModes.Semantic or SearchModes.Hybrid))
        {
            return Result.Failure<SearchResponse, Error>(Error.Validation(
                "search.mode.invalid", "Mode must be keyword, semantic or hybrid", "mode"));
        }

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

        return requestedMode switch
        {
            SearchModes.Keyword => await KeywordAsync(query, kinds, size, cancellationToken),
            SearchModes.Semantic => await SemanticAsync(query, kinds, size, cancellationToken),
            _ => await HybridAsync(query, kinds, size, cancellationToken),
        };
    }

    private async Task<Result<SearchResponse, Error>> KeywordAsync(
        string query, IReadOnlyCollection<string>? kinds, int size, CancellationToken cancellationToken)
    {
        try
        {
            var hits = await indexClient.SearchAsync(query, kinds, size, cancellationToken);
            return Success(query, SearchModes.Keyword, hits.Select(h => ToDto(h)).ToList());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Search query failed for {Query}", query);
            return Result.Failure<SearchResponse, Error>(
                Error.Unavailable("search.index.unavailable", "Search is temporarily unavailable"));
        }
    }

    private async Task<Result<SearchResponse, Error>> SemanticAsync(
        string query, IReadOnlyCollection<string>? kinds, int size, CancellationToken cancellationToken)
    {
        try
        {
            var hits = await semanticSearch.SearchAsync(query, kinds, size, cancellationToken);
            return Success(query, SearchModes.Semantic, hits.Select(h => ToDto(h)).ToList());
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Semantic search failed for {Query}", query);
            return Result.Failure<SearchResponse, Error>(
                Error.Unavailable("search.semantic.unavailable", "Semantic search is temporarily unavailable"));
        }
    }

    // Both sources are asked at once. Keyword failing is a failure as before; the semantic side failing or being slow
    // only costs the answer its semantic half.
    private async Task<Result<SearchResponse, Error>> HybridAsync(
        string query, IReadOnlyCollection<string>? kinds, int size, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(SemanticBudget);
        var semanticTask = semanticSearch.SearchAsync(query, kinds, CandidateDepth, budget.Token);

        IReadOnlyList<SearchHit> keywordHits;
        try
        {
            keywordHits = await indexClient.SearchAsync(query, kinds, CandidateDepth, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Search query failed for {Query}", query);
            return Result.Failure<SearchResponse, Error>(
                Error.Unavailable("search.index.unavailable", "Search is temporarily unavailable"));
        }

        IReadOnlyList<SemanticHit> semanticHits;
        try
        {
            semanticHits = await semanticTask;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Semantic side unavailable for {Query}; answering from keyword results", query);
            return Success(query, SearchModes.Keyword, keywordHits.Take(size).Select(h => ToDto(h)).ToList());
        }

        var byKey = new Dictionary<string, SearchResultDto>();
        foreach (var hit in keywordHits)
        {
            byKey.TryAdd(Key(hit.Kind, hit.SourceId), ToDto(hit));
        }

        foreach (var hit in semanticHits)
        {
            byKey.TryAdd(Key(hit.Kind, hit.SourceId), ToDto(hit));
        }

        var fused = ReciprocalRankFusion.Fuse([
            keywordHits.Select(h => Key(h.Kind, h.SourceId)).ToList(),
            semanticHits.Select(h => Key(h.Kind, h.SourceId)).ToList(),
        ]);

        var results = fused
            .Take(size)
            .Select(f => byKey[f.Key] with
            {
                Rank = f.Score,
                MatchedFields = MatchedFields(f.Key, keywordHits),
            })
            .ToList();

        return Success(query, SearchModes.Hybrid, results);
    }

    private static string[] MatchedFields(string key, IReadOnlyList<SearchHit> keywordHits) =>
        keywordHits.FirstOrDefault(h => Key(h.Kind, h.SourceId) == key)?.MatchedFields is { Length: > 0 } fields
            ? fields
            : ["semantic"];

    private static string Key(string kind, Guid sourceId) => $"{kind}:{sourceId}";

    private static SearchResultDto ToDto(SearchHit h) =>
        new(h.Kind, h.SourceId, h.Title, h.Subtitle, h.MatchedFields, h.Rank);

    // Similarity, not distance, so that a larger Rank is a better result in every mode.
    private static SearchResultDto ToDto(SemanticHit h) =>
        new(h.Kind, h.SourceId, h.Title, h.Subtitle, ["semantic"], 1 - h.Distance);

    private static Result<SearchResponse, Error> Success(string query, string mode, IReadOnlyList<SearchResultDto> results) =>
        Result.Success<SearchResponse, Error>(new SearchResponse(query, results, mode));
}
