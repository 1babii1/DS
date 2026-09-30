using System.Text;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SearchService.Domain;
using SearchService.Infrastructure.Elasticsearch;
using SearchService.Infrastructure.Postgres;
using SearchService.Infrastructure.Postgres.Embeddings;
using SearchService.Web.Consumers;

namespace SearchService.IntegrationTests;

// The semantic side is filled from the events the keyword side already consumes, without ever waiting on the model:
// the consumer only records what to embed, a worker makes the vectors. These tests hold that split, and what goes
// (and does not go) into a vector.
// The fake model keeps its switches (fail, delay, hook) in statics, so the classes that flip them must not run at the
// same time as each other.
[Collection(FakeEmbedderCollection.Name)]
public class EmbeddingStagingTests : IClassFixture<SearchTestWebFactory>, IAsyncLifetime
{
    private readonly Func<Task> _resetDatabase;
    private readonly IServiceProvider _services;
    private readonly DomainEventsConsumer _consumer;

    public EmbeddingStagingTests(SearchTestWebFactory factory)
    {
        _services = factory.Services;
        _resetDatabase = factory.ResetDatabaseAsync;
        _consumer = new DomainEventsConsumer(
            _services.GetRequiredService<IServiceScopeFactory>(),
            _services.GetRequiredService<SearchIndexClient>(),
            Options.Create(new DomainEventsConsumerOptions()),
            _services.GetRequiredService<ILogger<DomainEventsConsumer>>());
    }

    [Fact]
    public async Task A_new_department_is_staged_without_a_vector_and_the_consumer_does_not_call_the_model()
    {
        FakeEmbeddingClient.Fail = true;
        var id = Guid.NewGuid();

        Assert.True(Handle("directory.events", "DepartmentCreated", id, Department(id, "Marketing", "marketing")));

        var row = await Row(SearchDocument.EntityId(SearchKind.Department, id));
        Assert.Equal("Marketing (marketing)", row.Text);
        Assert.Null(row.Embedding);
        Assert.Equal("Marketing", row.Title);
        Assert.True(row.IsActive);
    }

    [Fact]
    public async Task The_worker_makes_the_vectors_and_a_redelivered_event_does_not_throw_them_away()
    {
        var id = Guid.NewGuid();
        Handle("directory.events", "DepartmentCreated", id, Department(id, "Marketing", "marketing"));
        Assert.Equal(1, await EmbedPending());
        Assert.Equal(FakeEmbeddingClient.For("Marketing (marketing)"), (await Row(DepartmentDocument(id))).Embedding);

        Handle("directory.events", "DepartmentCreated", id, Department(id, "Marketing", "marketing"));

        Assert.NotNull((await Row(DepartmentDocument(id))).Embedding);
        Assert.Equal(0, await EmbedPending());
    }

    [Fact]
    public async Task A_rename_clears_the_vector_and_the_next_pass_embeds_the_new_name()
    {
        var id = Guid.NewGuid();
        Handle("directory.events", "DepartmentCreated", id, Department(id, "Payments", "finance"));
        await EmbedPending();

        Handle("directory.events", "DepartmentRenamed", id, Department(id, "Treasury operations", "finance"));

        Assert.Null((await Row(DepartmentDocument(id))).Embedding);
        await EmbedPending();
        var row = await Row(DepartmentDocument(id));
        Assert.Equal("Treasury operations", row.Title);
        Assert.Equal(FakeEmbeddingClient.For("Treasury operations (finance)"), row.Embedding);
    }

    [Fact]
    public async Task A_deleted_department_leaves_no_row()
    {
        var id = Guid.NewGuid();
        Handle("directory.events", "DepartmentCreated", id, Department(id, "Temp", "temp"));

        Handle("directory.events", "DepartmentDeleted", id, $$"""{"DepartmentId":"{{id}}"}""");

        Assert.False(await Exists(DepartmentDocument(id)));
    }

    [Fact]
    public async Task What_is_embedded_for_an_employee_is_name_and_role_and_never_the_email()
    {
        var department = Guid.NewGuid();
        var position = Guid.NewGuid();
        var employee = Guid.NewGuid();
        Handle("directory.events", "DepartmentCreated", department, Department(department, "Engineering", "eng"));
        Handle("directory.events", "PositionCreated", position,
            $$"""{"PositionId":"{{position}}","Name":"Engineer","Description":null,"DepartmentIds":["{{department}}"]}""");

        Handle("employee.events", "EmployeeHired", employee,
            $$"""{"EmployeeId":"{{employee}}","FullName":"Maria Chen","Email":"maria.chen@example.com","DepartmentId":"{{department}}","PositionId":"{{position}}"}""");

        var row = await Row(SearchDocument.EntityId(SearchKind.Employee, employee));
        Assert.Equal("Maria Chen, Engineer · Engineering", row.Text);
        Assert.DoesNotContain("@", row.Text);
        Assert.DoesNotContain("example.com", row.Text);
    }

    [Fact]
    public async Task A_terminated_employee_keeps_the_vector_but_is_marked_inactive()
    {
        var employee = Guid.NewGuid();
        Handle("employee.events", "EmployeeHired", employee,
            $$"""{"EmployeeId":"{{employee}}","FullName":"Sam Lee","Email":"sam@example.com","DepartmentId":"{{Guid.NewGuid()}}","PositionId":"{{Guid.NewGuid()}}"}""");
        await EmbedPending();

        Handle("employee.events", "EmployeeTerminated", employee, $$"""{"EmployeeId":"{{employee}}"}""");

        var row = await Row(SearchDocument.EntityId(SearchKind.Employee, employee));
        Assert.False(row.IsActive);
        Assert.NotNull(row.Embedding);
    }

    [Fact]
    public async Task Positions_and_locations_are_staged_and_audit_documents_never_are()
    {
        var position = Guid.NewGuid();
        var location = Guid.NewGuid();

        Handle("directory.events", "PositionCreated", position,
            $$"""{"PositionId":"{{position}}","Name":"Site reliability engineer","Description":"Keeps the platform running","DepartmentIds":[]}""");
        Handle("directory.events", "LocationCreated", location,
            $$"""{"LocationId":"{{location}}","Name":"Head office","Street":"Main street 1","City":"Capital","Country":"Country","Timezone":"europe/asia"}""");

        Assert.True(await Exists(SearchDocument.EntityId(SearchKind.Position, position)));
        Assert.True(await Exists(SearchDocument.EntityId(SearchKind.Location, location)));
        Assert.Equal(2, await Count());
    }

    [Fact]
    public async Task A_model_that_is_down_leaves_rows_pending_without_failing_and_the_next_pass_completes_them()
    {
        var id = Guid.NewGuid();
        Handle("directory.events", "DepartmentCreated", id, Department(id, "Marketing", "marketing"));

        FakeEmbeddingClient.Fail = true;
        Assert.Equal(0, await EmbedPending());
        Assert.Null((await Row(DepartmentDocument(id))).Embedding);

        FakeEmbeddingClient.Fail = false;
        Assert.Equal(1, await EmbedPending());
        Assert.NotNull((await Row(DepartmentDocument(id))).Embedding);
    }

    // The model takes time. If the name changes meanwhile, the vector it returns belongs to the old name and must not
    // be stored as the vector of the new one.
    [Fact]
    public async Task A_vector_made_from_a_text_that_changed_meanwhile_is_not_stored()
    {
        var id = Guid.NewGuid();
        Handle("directory.events", "DepartmentCreated", id, Department(id, "Payments", "finance"));
        FakeEmbeddingClient.DuringEmbed = () =>
            Handle("directory.events", "DepartmentRenamed", id, Department(id, "Treasury operations", "finance"));

        Assert.Equal(0, await EmbedPending());
        FakeEmbeddingClient.DuringEmbed = null;

        Assert.Null((await Row(DepartmentDocument(id))).Embedding);
        await EmbedPending();
        Assert.Equal(FakeEmbeddingClient.For("Treasury operations (finance)"), (await Row(DepartmentDocument(id))).Embedding);
    }

    [Fact]
    public void An_employees_embedding_text_never_carries_contact_details()
    {
        var document = new SearchDocument(
            "employee:x", SearchKind.Employee, Guid.NewGuid(), "Maria Chen", "Engineer · Engineering",
            "Maria Chen maria@example.com", true, DateTime.UtcNow);

        Assert.DoesNotContain("maria@example.com", EmbeddingText.For(document));
    }

    public Task InitializeAsync()
    {
        FakeEmbeddingClient.Fail = false;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        FakeEmbeddingClient.Fail = false;
        FakeEmbeddingClient.DuringEmbed = null;
        await _resetDatabase();
    }

    private static string DepartmentDocument(Guid id) => SearchDocument.EntityId(SearchKind.Department, id);

    private static string Department(Guid id, string name, string identifier) =>
        $$"""{"DepartmentId":"{{id}}","Name":"{{name}}","Identifier":"{{identifier}}","ParentDepartmentId":null}""";

    private bool Handle(string topic, string type, Guid key, string payload) =>
        _consumer.HandleWithRetryAndDeadLetter(
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
            CancellationToken.None);

    private Task<int> EmbedPending() =>
        new DocumentEmbeddingWorker(
            _services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new EmbeddingsOptions()),
            _services.GetRequiredService<ILogger<DocumentEmbeddingWorker>>())
            .EmbedPendingAsync(CancellationToken.None);

    private async Task<DocumentEmbedding> Row(string documentId)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SearchDbContext>()
            .DocumentEmbeddings.AsNoTracking().SingleAsync(e => e.DocumentId == documentId);
    }

    private async Task<bool> Exists(string documentId)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SearchDbContext>()
            .DocumentEmbeddings.AnyAsync(e => e.DocumentId == documentId);
    }

    private async Task<int> Count()
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SearchDbContext>().DocumentEmbeddings.CountAsync();
    }
}
