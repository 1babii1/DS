using System.Reflection;
using System.Text;
using Confluent.Kafka;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SearchService.Domain;
using SearchService.Infrastructure.Elasticsearch;
using SearchService.Infrastructure.Postgres;
using SearchService.Web;
using SearchService.Web.Consumers;
using SearchService.Web.Controllers;

namespace SearchService.IntegrationTests;

// Erasing a person from the search index (ADR 0047): their employee document and its staged embedding go, so do the audit-kind documents that
// carry their identifier, and nothing for them is indexed again. Real Elasticsearch, real Postgres, the real consumer.
[Collection(FakeEmbedderCollection.Name)]
public class SubjectErasureTests : IClassFixture<SearchTestWebFactory>, IAsyncLifetime
{
    private readonly Func<Task> _resetDatabase;
    private readonly IServiceProvider _services;
    private readonly SearchIndexClient _index;
    private readonly DomainEventsConsumer _consumer;

    public SubjectErasureTests(SearchTestWebFactory factory)
    {
        _services = factory.Services;
        _resetDatabase = factory.ResetDatabaseAsync;
        _index = _services.GetRequiredService<SearchIndexClient>();
        _consumer = new DomainEventsConsumer(
            _services.GetRequiredService<IServiceScopeFactory>(),
            _index,
            Options.Create(new DomainEventsConsumerOptions()),
            _services.GetRequiredService<ILogger<DomainEventsConsumer>>());
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _resetDatabase();

    private void Consume(string topic, string key, string type, string payload)
    {
        var headers = new Headers
        {
            { "message-id", Encoding.UTF8.GetBytes(Guid.NewGuid().ToString()) },
            { "message-type", Encoding.UTF8.GetBytes(type) },
        };
        Assert.True(_consumer.HandleWithRetryAndDeadLetter(
            new ConsumeResult<string, string>
            {
                Topic = topic,
                Message = new Message<string, string> { Key = key, Value = payload, Headers = headers },
            },
            CancellationToken.None));
    }

    private Guid Hire(string name, string email)
    {
        var employee = Guid.NewGuid();
        Consume("employee.events", employee.ToString(), "EmployeeHired",
            $$"""{"EmployeeId":"{{employee}}","FullName":"{{name}}","Email":"{{email}}","DepartmentId":"{{Guid.NewGuid()}}","PositionId":"{{Guid.NewGuid()}}"}""");
        return employee;
    }

    private async Task<ErasureResult> Erase(params string[] subjects)
    {
        await using var scope = _services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<SubjectErasure>().EraseAsync(subjects, CancellationToken.None);
        await _index.RefreshAsync(CancellationToken.None);
        return result;
    }

    // Only the audit-kind documents: the index is shared by the tests of this class, and a search for an address also matches other people's, by its tokens.
    private async Task<int> AuditHits(string query)
    {
        await _index.RefreshAsync(CancellationToken.None);
        return (await _index.SearchAsync(query, [SearchKind.Audit], 20, CancellationToken.None)).Count;
    }

    private async Task<int> Found(string query)
    {
        await _index.RefreshAsync(CancellationToken.None);
        return (await _index.SearchAsync(query, null, 20, CancellationToken.None)).Count;
    }

    [Fact]
    public async Task Erasing_an_employee_removes_their_document_and_their_staged_embedding()
    {
        var employee = Hire("Zaphod Quibblewick", "zaphod.q@example.test");
        var documentId = SearchDocument.EntityId(SearchKind.Employee, employee);
        Assert.NotNull(await _index.GetAsync(documentId, CancellationToken.None));
        Assert.True(await Found("Quibblewick") > 0);

        var result = await Erase(employee.ToString());

        Assert.Equal(1, result.EmployeeDocuments);
        Assert.Null(await _index.GetAsync(documentId, CancellationToken.None));
        Assert.Equal(0, await Found("Quibblewick"));
        await using var scope = _services.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<SearchDbContext>().DocumentEmbeddings.AnyAsync(e => e.DocumentId == documentId));
    }

    [Fact]
    public async Task Erasing_an_address_removes_the_audit_documents_that_carry_it()
    {
        const string address = "wanderer.zz@example.test";
        Consume("auth.events", address, "LoginFailed", $$"""{"Email":"{{address}}","Reason":"bad password","IpAddress":"203.0.113.7"}""");
        Assert.True((await AuditHits("wanderer.zz@example.test")) > 0, "the failed sign-in was not indexed, so there is nothing for the test to erase");

        var result = await Erase(address);

        Assert.True(result.AuditDocuments >= 1);
        Assert.Equal(0, await AuditHits("wanderer.zz@example.test"));
    }

    [Fact]
    public async Task Another_person_is_left_in_the_index()
    {
        var gone = Hire("Arthur Fenchurch", "arthur.f@example.test");
        var kept = Hire("Trillian Mcmillan", "trillian.m@example.test");

        await Erase(gone.ToString());

        Assert.Null(await _index.GetAsync(SearchDocument.EntityId(SearchKind.Employee, gone), CancellationToken.None));
        Assert.NotNull(await _index.GetAsync(SearchDocument.EntityId(SearchKind.Employee, kept), CancellationToken.None));
    }

    [Fact]
    public async Task A_late_or_replayed_event_for_an_erased_person_indexes_nothing()
    {
        var employee = Hire("Ford Prefectson", "ford.p@example.test");
        await Erase(employee.ToString());

        Consume("employee.events", employee.ToString(), "EmployeeHired",
            $$"""{"EmployeeId":"{{employee}}","FullName":"Ford Prefectson","Email":"ford.p@example.test","DepartmentId":"{{Guid.NewGuid()}}","PositionId":"{{Guid.NewGuid()}}"}""");

        Assert.Null(await _index.GetAsync(SearchDocument.EntityId(SearchKind.Employee, employee), CancellationToken.None));
        Assert.Equal(0, await Found("Prefectson"));
    }

    [Fact]
    public async Task Erasing_twice_is_harmless()
    {
        var employee = Hire("Marvin Paranoid", "marvin.p@example.test");

        await Erase(employee.ToString());
        var second = await Erase(employee.ToString());

        Assert.Equal(0, second.EmployeeDocuments);
    }

    [Fact]
    public async Task The_erase_endpoint_refuses_none_and_too_many_subjects()
    {
        await using var scope = _services.CreateAsyncScope();
        var controller = new SubjectsController(scope.ServiceProvider.GetRequiredService<SubjectErasure>());

        var none = await controller.Erase(new EraseSubjectsRequest([]), CancellationToken.None);
        var many = await controller.Erase(
            new EraseSubjectsRequest(Enumerable.Range(0, 51).Select(i => Guid.NewGuid().ToString()).ToArray()), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(none.Result);
        Assert.IsType<BadRequestObjectResult>(many.Result);
    }

    [Fact]
    public async Task Every_policy_a_controller_asks_for_is_registered()
    {
        var provider = _services.GetRequiredService<IAuthorizationPolicyProvider>();
        var named = typeof(SubjectsController).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t))
            .SelectMany(t => t.GetCustomAttributes<AuthorizeAttribute>(true)
                .Concat(t.GetMethods().SelectMany(m => m.GetCustomAttributes<AuthorizeAttribute>(true))))
            .Select(a => a.Policy)
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct()
            .ToList();

        Assert.Contains("StepUp", named);
        Assert.Contains("IsAdmin", named);
        foreach (var policy in named)
        {
            Assert.True(await provider.GetPolicyAsync(policy!) is not null, $"policy '{policy}' is asked for by a controller and never registered");
        }
    }
}
