using System.Globalization;
using System.Text;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SearchService.Infrastructure.Elasticsearch;
using SearchService.Infrastructure.Postgres;
using SearchService.Infrastructure.Postgres.Embeddings;
using SearchService.Web.Consumers;
using SearchService.Web.Controllers;
using Xunit.Abstractions;

namespace SearchService.IntegrationTests.Eval;

// Skipped unless SEARCH_EVAL=1: it needs a running Ollama with nomic-embed-text (the model the service uses), takes
// a while, and prints a table rather than asserting one. Run it with
//   SEARCH_EVAL=1 dotnet test backend/SearchService/SearchService.IntegrationTests --filter "Category=Eval" \
//     --logger "console;verbosity=detailed"
// and, to also get the table as a file, SEARCH_EVAL_OUT=<path>. The stack under test is the real one (Elasticsearch and
// pgvector in containers, the consumer and the semantic search as shipped); only the model is outside them.
public sealed class EvalFactAttribute : FactAttribute
{
    public EvalFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SEARCH_EVAL") != "1")
        {
            Skip = "Set SEARCH_EVAL=1 to run the search quality evaluation (needs Ollama).";
        }
    }
}

[Trait("Category", "Eval")]
public class SearchQualityEval(SearchTestWebFactory factory, ITestOutputHelper output)
    : IClassFixture<SearchTestWebFactory>, IAsyncLifetime
{
    private static readonly string[] Modes = ["keyword", "semantic", "hybrid"];
    private static readonly int[] Ks = [1, 3, 5];

    private readonly IServiceProvider _services = factory.Services;

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => factory.ResetDatabaseAsync();

    [EvalFact]
    public async Task Measure_recall_and_mrr_of_keyword_semantic_and_hybrid_search()
    {
        var ollama = Environment.GetEnvironmentVariable("SEARCH_EVAL_OLLAMA") ?? "http://localhost:11434";
        var embeddings = new OllamaEmbeddingClient(
            new HttpClient { BaseAddress = new Uri(ollama), Timeout = TimeSpan.FromMinutes(2) },
            Options.Create(new EmbeddingsOptions()));

        await LoadFixtureThroughTheConsumer();
        await EmbedEverything(embeddings);

        var indexClient = _services.GetRequiredService<SearchIndexClient>();
        await indexClient.RefreshAsync(CancellationToken.None);

        var outcomes = new List<Outcome>();
        foreach (var query in EvalCorpus.Queries)
        {
            foreach (var mode in Modes)
            {
                await using var scope = _services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<SearchDbContext>();
                var controller = new SearchController(
                    indexClient, new SemanticSearch(db, embeddings), NullLogger<SearchController>.Instance);

                var titles = await Titles(controller, query, mode);
                outcomes.Add(Score(query, mode, titles));
            }
        }

        var report = Report(outcomes, embeddings);
        output.WriteLine(report);
        if (Environment.GetEnvironmentVariable("SEARCH_EVAL_OUT") is { Length: > 0 } path)
        {
            await File.WriteAllTextAsync(path, report);
        }

        Assert.NotEmpty(outcomes);
    }

    private async Task LoadFixtureThroughTheConsumer()
    {
        var consumer = new DomainEventsConsumer(
            _services.GetRequiredService<IServiceScopeFactory>(),
            _services.GetRequiredService<SearchIndexClient>(),
            Options.Create(new DomainEventsConsumerOptions()),
            _services.GetRequiredService<ILogger<DomainEventsConsumer>>());

        foreach (var name in EvalCorpus.Departments)
        {
            var id = Guid.NewGuid();
            Send(consumer, "DepartmentCreated", id,
                $$"""{"DepartmentId":"{{id}}","Name":"{{name}}","Identifier":"{{name.ToLowerInvariant().Replace(' ', '-')}}","ParentDepartmentId":null}""");
        }

        foreach (var name in EvalCorpus.Positions)
        {
            var id = Guid.NewGuid();
            Send(consumer, "PositionCreated", id,
                $$"""{"PositionId":"{{id}}","Name":"{{name}}","Description":null,"DepartmentIds":[]}""");
        }

        await Task.CompletedTask;
    }

    private static void Send(DomainEventsConsumer consumer, string type, Guid key, string payload) =>
        Assert.True(consumer.HandleWithRetryAndDeadLetter(
            new ConsumeResult<string, string>
            {
                Topic = "directory.events",
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

    // The shipped worker takes its model from DI (a fake in this project), so the real model is applied to the same
    // staged rows directly.
    private async Task EmbedEverything(IEmbeddingClient embeddings)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SearchDbContext>();
        foreach (var row in await db.DocumentEmbeddings.Where(e => e.Embedding == null).ToListAsync())
        {
            row.Embedding = await embeddings.EmbedAsync(row.Text, CancellationToken.None);
        }

        await db.SaveChangesAsync();
    }

    private static async Task<List<string>> Titles(SearchController controller, EvalCorpus.EvalQuery query, string mode)
    {
        var endpoint = await controller.Search(query.Text, query.Type, 10, mode, CancellationToken.None);
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.Response.Body = new MemoryStream();
        await endpoint.ExecuteAsync(http);
        http.Response.Body.Seek(0, SeekOrigin.Begin);
        using var doc = await System.Text.Json.JsonDocument.ParseAsync(http.Response.Body);

        Assert.Equal(200, http.Response.StatusCode);
        return doc.RootElement.GetProperty("result").GetProperty("results").EnumerateArray()
            .Select(r => r.GetProperty("title").GetString()!)
            .ToList();
    }

    private static Outcome Score(EvalCorpus.EvalQuery query, string mode, List<string> titles)
    {
        var firstRelevantRank = titles.FindIndex(t => query.Relevant.Contains(t)) + 1;
        var recall = Ks.ToDictionary(
            k => k,
            k => (double)titles.Take(k).Count(t => query.Relevant.Contains(t)) / query.Relevant.Length);
        return new Outcome(query, mode, firstRelevantRank == 0 ? 0 : 1d / firstRelevantRank, recall, titles.Take(3).ToList());
    }

    private static string Report(List<Outcome> outcomes, IEmbeddingClient _)
    {
        var text = new StringBuilder();
        text.AppendLine($"Search quality: {EvalCorpus.Queries.Length} queries over {EvalCorpus.Departments.Length} departments and {EvalCorpus.Positions.Length} positions, model nomic-embed-text.");
        text.AppendLine("recall@k = share of the relevant items found in the top k (averaged over queries); MRR = mean of 1/rank of the first relevant item.");
        text.AppendLine();

        void Table(string title, Func<Outcome, bool> filter)
        {
            var count = outcomes.Where(filter).Select(o => o.Query).Distinct().Count();
            text.AppendLine($"{title} ({count} queries)");
            text.AppendLine("| mode | recall@1 | recall@3 | recall@5 | MRR |");
            text.AppendLine("|---|---|---|---|---|");
            foreach (var mode in Modes)
            {
                var rows = outcomes.Where(o => o.Mode == mode && filter(o)).ToList();
                string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
                text.AppendLine(
                    $"| {mode} | {F(rows.Average(r => r.Recall[1]))} | {F(rows.Average(r => r.Recall[3]))} | {F(rows.Average(r => r.Recall[5]))} | {F(rows.Average(r => r.Mrr))} |");
            }

            text.AppendLine();
        }

        Table("All queries", _ => true);
        foreach (var kind in Enum.GetValues<EvalCorpus.QueryKind>())
        {
            Table($"{kind} queries", o => o.Query.Kind == kind);
        }

        text.AppendLine("Queries where the first relevant item was not in the top 3, per mode:");
        foreach (var mode in Modes)
        {
            foreach (var miss in outcomes.Where(o => o.Mode == mode && o.Recall[3] == 0))
            {
                text.AppendLine($"- [{mode}] \"{miss.Query.Text}\" wanted {string.Join(" / ", miss.Query.Relevant)}, got {string.Join(", ", miss.Top)}");
            }
        }

        return text.ToString();
    }

    private sealed record Outcome(
        EvalCorpus.EvalQuery Query, string Mode, double Mrr, Dictionary<int, double> Recall, List<string> Top);
}
