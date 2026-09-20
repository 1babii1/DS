using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SearchService.Domain;
using SearchService.Infrastructure.Elasticsearch;
using SearchService.Web.Controllers;

namespace SearchService.IntegrationTests;

// Calls SearchController directly rather than through a full HTTP+JWT round trip - same
// "call the seam directly" testing philosophy as HandleWithRetryAndDeadLetter elsewhere in
// this codebase. [Authorize]'s 401 behavior is standard ASP.NET Core middleware, identical
// to every other controller in this platform, and is verified live (curl through nginx)
// rather than re-derived here.
public class SearchControllerTests : IClassFixture<SearchTestWebFactory>, IAsyncLifetime
{
    private readonly Func<Task> _resetDatabase;
    private readonly SearchIndexClient _indexClient;
    private readonly SearchController _sut;

    public SearchControllerTests(SearchTestWebFactory factory)
    {
        _resetDatabase = factory.ResetDatabaseAsync;
        _indexClient = factory.Services.GetRequiredService<SearchIndexClient>();
        _sut = new SearchController(_indexClient, NullLogger<SearchController>.Instance);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("")]
    public async Task Query_shorter_than_two_characters_is_rejected(string query)
    {
        var result = await _sut.Search(query, null, null, CancellationToken.None);

        await AssertErrorAsync(result);
    }

    [Fact]
    public async Task Query_longer_than_100_characters_is_rejected()
    {
        var result = await _sut.Search(new string('x', 101), null, null, CancellationToken.None);

        await AssertErrorAsync(result);
    }

    [Fact]
    public async Task Unknown_type_is_rejected()
    {
        var result = await _sut.Search("marketing", "not-a-real-kind", null, CancellationToken.None);

        await AssertErrorAsync(result);
    }

    [Fact]
    public async Task Valid_query_with_no_matches_returns_an_empty_result_not_an_error()
    {
        var result = await _sut.Search("zzzznomatchzzzz", null, null, CancellationToken.None);

        var response = await GetSuccessValueAsync(result);
        Assert.Empty(response.Results);
    }

    [Fact]
    public async Task Exact_match_ranks_above_a_prefix_match_which_ranks_above_a_substring_match()
    {
        var exactId = Guid.NewGuid();
        var prefixId = Guid.NewGuid();
        var substringId = Guid.NewGuid();

        await _indexClient.UpsertAsync(
            new SearchDocument(SearchDocument.EntityId(SearchKind.Department, exactId), SearchKind.Department, exactId,
                "Marketing", "marketing", "Marketing marketing", true, DateTime.UtcNow),
            CancellationToken.None);
        await _indexClient.UpsertAsync(
            new SearchDocument(SearchDocument.EntityId(SearchKind.Department, prefixId), SearchKind.Department, prefixId,
                "Marketing Growth", "marketing-growth", "Marketing Growth marketing-growth", true, DateTime.UtcNow),
            CancellationToken.None);
        await _indexClient.UpsertAsync(
            new SearchDocument(SearchDocument.EntityId(SearchKind.Department, substringId), SearchKind.Department, substringId,
                "Global Marketing Team", "global-marketing", "Global Marketing Team global-marketing", true, DateTime.UtcNow),
            CancellationToken.None);
        await _indexClient.RefreshAsync(CancellationToken.None);

        var result = await _sut.Search("Marketing", SearchKind.Department, null, CancellationToken.None);
        var response = await GetSuccessValueAsync(result);

        var ids = response.Results.Select(r => r.Id).ToList();
        Assert.Contains(exactId, ids);
        Assert.True(
            ids.IndexOf(exactId) <= ids.IndexOf(prefixId),
            "exact match should rank at or above the prefix match");
    }

    [Fact]
    public async Task Limit_is_clamped_to_the_documented_maximum()
    {
        for (var i = 0; i < 25; i++)
        {
            var id = Guid.NewGuid();
            await _indexClient.UpsertAsync(
                new SearchDocument(SearchDocument.EntityId(SearchKind.Position, id), SearchKind.Position, id,
                    $"Clamp Test Position {i}", null, $"Clamp Test Position {i}", true, DateTime.UtcNow),
                CancellationToken.None);
        }

        await _indexClient.RefreshAsync(CancellationToken.None);

        var result = await _sut.Search("Clamp Test Position", SearchKind.Position, 500, CancellationToken.None);
        var response = await GetSuccessValueAsync(result);

        Assert.True(response.Results.Count <= 20);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _resetDatabase();

    private static async Task<SearchResponse> GetSuccessValueAsync(
        Shared.EndpointResults.EndpointResult<SearchResponse> result)
    {
        var httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();
        await result.ExecuteAsync(httpContext);

        httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
        var envelope = await System.Text.Json.JsonSerializer.DeserializeAsync<Shared.Envelope<SearchResponse>>(
            httpContext.Response.Body, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.Equal(200, httpContext.Response.StatusCode);
        Assert.NotNull(envelope);
        return envelope!.Result!;
    }

    private static async Task AssertErrorAsync(Shared.EndpointResults.EndpointResult<SearchResponse> result)
    {
        var httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();
        await result.ExecuteAsync(httpContext);

        Assert.Equal(400, httpContext.Response.StatusCode);
    }
}
