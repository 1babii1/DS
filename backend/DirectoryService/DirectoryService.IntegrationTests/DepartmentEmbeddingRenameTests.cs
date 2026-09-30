using System.Text;
using Confluent.Kafka;
using DirectoryService.Application.Department;
using DirectoryService.Application.Department.Commands;
using DirectoryService.Application.Search;
using DirectoryService.Contracts.Request.Department;
using DirectoryService.Domain.Departments.ValueObjects;
using DirectoryService.Domain.Locations;
using DirectoryService.Domain.Locations.ValueObjects;
using DirectoryService.Infrastructure.Postgres;
using DirectoryService.Infrastructure.Postgres.Embeddings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DirectoryService.IntegrationTests;

// The whole point of renaming being observable: after a rename, a department is found semantically by what its new
// name means, and its embedding is no longer the one made from the old name. The consumer drops the stale embedding;
// the worker makes the new one; neither calls a model in the consumer's path.
public class DepartmentEmbeddingRenameTests : IClassFixture<DirectoryTestWEbFactory>, IAsyncLifetime
{
    private readonly Func<Task> _resetDatabase;
    private readonly IServiceProvider _services;

    public DepartmentEmbeddingRenameTests(DirectoryTestWEbFactory factory)
    {
        _services = factory.Services;
        _resetDatabase = factory.ResetDatabaseAsync;
    }

    [Fact]
    public async Task After_a_rename_the_department_is_found_by_its_new_name_and_its_embedding_is_the_new_one()
    {
        var payments = await CreateDepartment("Payments", "finance");
        var gardening = await CreateDepartment("Gardening", "garden");
        await EmbedPending();
        Assert.Equal(FakeEmbeddingClient.For("Payments (finance)"), await EmbeddingOf(payments));

        await Rename(payments, "Treasury operations");
        Assert.True(Consume(RenamedMessage(payments, "Treasury operations", "finance")));

        // Dropped at once, and only for the renamed department.
        Assert.Null(await EmbeddingOf(payments));
        Assert.NotNull(await EmbeddingOf(gardening));

        await EmbedPending();

        Assert.Equal(FakeEmbeddingClient.For("Treasury operations (finance)"), await EmbeddingOf(payments));
        var found = await Search("Treasury operations");
        Assert.Equal(payments, found.First().Id);
        Assert.Equal("Treasury operations", found.First().Name);
    }

    [Fact]
    public async Task Handling_the_same_rename_twice_is_harmless()
    {
        var payments = await CreateDepartment("Payments", "finance");
        await EmbedPending();
        await Rename(payments, "Treasury operations");

        Assert.True(Consume(RenamedMessage(payments, "Treasury operations", "finance")));
        Assert.True(Consume(RenamedMessage(payments, "Treasury operations", "finance")));
        await EmbedPending();

        Assert.Equal(FakeEmbeddingClient.For("Treasury operations (finance)"), await EmbeddingOf(payments));
    }

    [Fact]
    public async Task Other_events_on_the_topic_leave_embeddings_alone()
    {
        var payments = await CreateDepartment("Payments", "finance");
        await EmbedPending();

        Assert.True(Consume(Message("DepartmentMoved", payments.ToString(), $$"""{"DepartmentId":"{{payments}}"}""")));

        Assert.NotNull(await EmbeddingOf(payments));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _resetDatabase();

    private bool Consume(ConsumeResult<string, string> message) =>
        new DepartmentRenamedEmbeddingConsumer(
            _services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new DepartmentRenamedEmbeddingConsumerOptions()),
            _services.GetRequiredService<ILogger<DepartmentRenamedEmbeddingConsumer>>())
            .HandleWithRetryAndDeadLetter(message, CancellationToken.None);

    private Task EmbedPending() =>
        new DepartmentEmbeddingWorker(
            _services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new EmbeddingsOptions()),
            _services.GetRequiredService<ILogger<DepartmentEmbeddingWorker>>())
            .EmbedPendingDepartmentsAsync(CancellationToken.None);

    private async Task<Pgvector.Vector?> EmbeddingOf(Guid departmentId)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DirectoryServiceDbContext>();
        return await db.DepartmentEmbeddings.AsNoTracking()
            .Where(e => e.DepartmentId == departmentId)
            .Select(e => e.Embedding)
            .SingleOrDefaultAsync();
    }

    private async Task<IReadOnlyList<Contracts.Response.Department.DepartmentSearchResultDto>> Search(string query)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDepartmentSemanticSearch>()
            .SearchAsync(query, 5, CancellationToken.None);
    }

    private async Task Rename(Guid id, string name)
    {
        await using var scope = _services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<RenameDepartmentHandler>()
            .Handle(new RenameDepartmentCommand(id, new RenameDepartmentRequest(name)), CancellationToken.None);
        Assert.True(result.IsSuccess);
    }

    private async Task<Guid> CreateDepartment(string name, string identifier)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DirectoryServiceDbContext>();
        var location = new Location(
            LocationId.NewLocationId(),
            LocationName.Create($"Office {identifier}").Value,
            Timezone.Create("europe/asia").Value,
            Address.Create($"Main street {identifier}", "Capital", "Country").Value,
            []);
        db.Locations.Add(location);
        await db.SaveChangesAsync();

        var result = await scope.ServiceProvider.GetRequiredService<CreateDepartmentHandler>().Handle(
            new CreateDepartmentCommand(new CreateDepartmentRequest(
                DepartmentName.Create(name).Value,
                DepartmentIdentifier.Create(identifier).Value,
                null,
                null,
                [location.Id],
                DepartmentId.NewDepartmentId())),
            CancellationToken.None);
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    private static ConsumeResult<string, string> RenamedMessage(Guid id, string name, string identifier) =>
        Message("DepartmentRenamed", id.ToString(), $$"""{"DepartmentId":"{{id}}","Name":"{{name}}","Identifier":"{{identifier}}"}""");

    private static ConsumeResult<string, string> Message(string type, string key, string payload) =>
        new()
        {
            Topic = "directory.events",
            Message = new Message<string, string>
            {
                Key = key,
                Value = payload,
                Headers = new Headers
                {
                    { "message-id", Encoding.UTF8.GetBytes(Guid.NewGuid().ToString()) },
                    { "message-type", Encoding.UTF8.GetBytes(type) },
                },
            },
        };
}
