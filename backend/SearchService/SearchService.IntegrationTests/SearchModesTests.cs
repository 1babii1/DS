using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SearchService.Domain;
using SearchService.Infrastructure.Elasticsearch;
using SearchService.Infrastructure.Postgres.Embeddings;
using SearchService.Web.Consumers;
using SearchService.Web.Controllers;

namespace SearchService.IntegrationTests;

// One endpoint, three ways to answer it. Keyword matches the words typed; semantic matches what they mean (here, words
// a real model would treat as close); hybrid fuses both. A semantic side that is missing, broken or slow must cost the
// answer only its semantic half.
public class SearchModesTests : IClassFixture<SearchTestWebFactory>, IAsyncLifetime
{
    private readonly Func<Task> _resetDatabase;
    private readonly IServiceProvider _services;
    private readonly DomainEventsConsumer _consumer;
    private readonly SearchIndexClient _indexClient;

    public SearchModesTests(SearchTestWebFactory factory)
    {
        _services = factory.Services;
        _resetDatabase = factory.ResetDatabaseAsync;
        _indexClient = _services.GetRequiredService<SearchIndexClient>();
        _consumer = new DomainEventsConsumer(
            _services.GetRequiredService<IServiceScopeFactory>(),
            _indexClient,
            Options.Create(new DomainEventsConsumerOptions()),
            _services.GetRequiredService<ILogger<DomainEventsConsumer>>());
    }

    [Fact]
    public async Task Semantic_finds_what_the_words_typed_do_not_match_and_keyword_does_not()
    {
        var billing = await Department("Billing team", "bill-desk");
        await Embed();

        var keyword = await Search("invoices", "keyword");
        var semantic = await Search("invoices", "semantic");
        var hybrid = await Search("invoices", "hybrid");

        Assert.DoesNotContain(keyword.Results, r => r.Id == billing);
        Assert.Contains(semantic.Results, r => r.Id == billing);
        Assert.Contains(hybrid.Results, r => r.Id == billing);
        Assert.Equal("keyword", keyword.Mode);
        Assert.Equal("semantic", semantic.Mode);
        Assert.Equal("hybrid", hybrid.Mode);
        Assert.Contains("semantic", hybrid.Results.Single(r => r.Id == billing).MatchedFields);
    }

    [Fact]
    public async Task A_document_both_sources_agree_on_outranks_one_only_semantic_found()
    {
        var both = await Department("Invoices archive", "archive");
        var semanticOnly = await Department("Billing team", "bill-desk");
        await Embed();

        var hybrid = await Search("invoices", "hybrid");

        Assert.Equal(both, hybrid.Results[0].Id);
        Assert.Contains(hybrid.Results, r => r.Id == semanticOnly);
        Assert.True(hybrid.Results[0].Rank > hybrid.Results.Single(r => r.Id == semanticOnly).Rank);
    }

    [Fact]
    public async Task The_default_mode_is_hybrid()
    {
        var billing = await Department("Billing team", "bill-desk");
        await Embed();

        var response = await Search("invoices", null);

        Assert.Equal("hybrid", response.Mode);
        Assert.Contains(response.Results, r => r.Id == billing);
    }

    [Fact]
    public async Task Types_narrow_the_semantic_side_too()
    {
        var department = await Department("Billing team", "bill-desk");
        var position = Guid.NewGuid();
        Handle("directory.events", "PositionCreated", position,
            $$"""{"PositionId":"{{position}}","Name":"Billing clerk","Description":null,"DepartmentIds":[]}""");
        await Embed();

        var onlyPositions = await Search("invoices", "semantic", "position");

        Assert.Equal(position, Assert.Single(onlyPositions.Results).Id);
        Assert.DoesNotContain(onlyPositions.Results, r => r.Id == department);
    }

    [Fact]
    public async Task Rows_without_a_vector_yet_are_not_semantic_candidates_but_keyword_still_finds_them()
    {
        var invoices = await Department("Invoices archive", "archive");

        var semantic = await Search("invoices", "semantic");
        var hybrid = await Search("invoices", "hybrid");

        Assert.Empty(semantic.Results);
        Assert.Contains(hybrid.Results, r => r.Id == invoices);
    }

    [Fact]
    public async Task When_the_model_is_down_hybrid_answers_from_keyword_and_says_so_and_semantic_is_unavailable()
    {
        var invoices = await Department("Invoices archive", "archive");
        await Embed();
        FakeEmbeddingClient.Fail = true;

        var hybrid = await Search("invoices", "hybrid");
        var keyword = await Search("invoices", "keyword");
        var semantic = await SearchRaw("invoices", "semantic");

        Assert.Equal("keyword", hybrid.Mode);
        Assert.Contains(hybrid.Results, r => r.Id == invoices);
        Assert.Contains(keyword.Results, r => r.Id == invoices);
        Assert.Equal("search.semantic.unavailable", semantic.ErrorCode);
    }

    [Fact]
    public async Task A_model_that_does_not_answer_in_time_costs_hybrid_only_its_semantic_half()
    {
        var invoices = await Department("Invoices archive", "archive");
        await Embed();
        FakeEmbeddingClient.Delay = TimeSpan.FromSeconds(30);
        var started = DateTime.UtcNow;

        var hybrid = await Search("invoices", "hybrid");

        Assert.Equal("keyword", hybrid.Mode);
        Assert.Contains(hybrid.Results, r => r.Id == invoices);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData("fuzzy")]
    [InlineData("SEMANTICALLY")]
    public async Task An_unknown_mode_is_rejected(string mode)
    {
        var result = await SearchRaw("invoices", mode);

        Assert.Equal("search.mode.invalid", result.ErrorCode);
    }

    public Task InitializeAsync()
    {
        Reset();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        Reset();
        await _resetDatabase();
    }

    private static void Reset()
    {
        FakeEmbeddingClient.Fail = false;
        FakeEmbeddingClient.Delay = TimeSpan.Zero;
        FakeEmbeddingClient.DuringEmbed = null;
    }

    private async Task<Guid> Department(string name, string identifier)
    {
        var id = Guid.NewGuid();
        Handle("directory.events", "DepartmentCreated", id,
            $$"""{"DepartmentId":"{{id}}","Name":"{{name}}","Identifier":"{{identifier}}","ParentDepartmentId":null}""");
        await _indexClient.RefreshAsync(CancellationToken.None);
        return id;
    }

    private void Handle(string topic, string type, Guid key, string payload)
    {
        Assert.True(_consumer.HandleWithRetryAndDeadLetter(
            new ConsumeResult<string, string>
            {
                Topic = topic,
                Message = new Message<string, string>
                {
                    Key = key.ToString(),
                    Value = payload,
                    Headers = new Headers
                    {
                        { "message-id", Encoding.UTF8.GetBytes(Guid.NewGuid().ToString()) },
                        { "message-type", Encoding.UTF8.GetBytes(type) },
                    },
                },
            },
            CancellationToken.None));
    }

    private async Task Embed()
    {
        await new DocumentEmbeddingWorker(
                _services.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new EmbeddingsOptions()),
                _services.GetRequiredService<ILogger<DocumentEmbeddingWorker>>())
            .EmbedPendingAsync(CancellationToken.None);
        await _indexClient.RefreshAsync(CancellationToken.None);
    }

    private async Task<SearchResponse> Search(string query, string? mode, string? types = null)
    {
        var outcome = await SearchRaw(query, mode, types);
        Assert.True(outcome.ErrorCode is null, $"search failed with {outcome.ErrorCode}");
        return outcome.Value!;
    }

    private async Task<Outcome> SearchRaw(string query, string? mode, string? types = null)
    {
        await using var scope = _services.CreateAsyncScope();
        var controller = new SearchController(
            _indexClient, scope.ServiceProvider.GetRequiredService<SemanticSearch>(), NullLogger<SearchController>.Instance);
        var endpoint = await controller.Search(query, types, 20, mode, CancellationToken.None);

        // EndpointResult is an IResult: running it is the real contract, so the answer is read from the response.
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.Response.Body = new MemoryStream();
        await endpoint.ExecuteAsync(http);
        http.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(http.Response.Body).ReadToEndAsync();

        if (http.Response.StatusCode != 200)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            return new Outcome(null, doc.RootElement.GetProperty("error").GetProperty("messages")[0].GetProperty("code").GetString());
        }

        var envelope = System.Text.Json.JsonSerializer.Deserialize<Shared.Envelope<SearchResponse>>(
            body, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return new Outcome(envelope!.Result, null);
    }

    private sealed record Outcome(SearchResponse? Value, string? ErrorCode);
}
