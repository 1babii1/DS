using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SearchService.Domain;
using SearchService.Infrastructure.Elasticsearch;
using SearchService.Web.Consumers;

namespace SearchService.IntegrationTests;

public class DomainEventsConsumerTests : IClassFixture<SearchTestWebFactory>, IAsyncLifetime
{
    private readonly Func<Task> _resetDatabase;
    private readonly IServiceProvider _services;
    private readonly SearchIndexClient _indexClient;
    private readonly DomainEventsConsumer _sut;

    public DomainEventsConsumerTests(SearchTestWebFactory factory)
    {
        _services = factory.Services;
        _resetDatabase = factory.ResetDatabaseAsync;
        _indexClient = _services.GetRequiredService<SearchIndexClient>();

        _sut = new DomainEventsConsumer(
            _services.GetRequiredService<IServiceScopeFactory>(),
            _indexClient,
            Options.Create(new DomainEventsConsumerOptions()),
            _services.GetRequiredService<ILogger<DomainEventsConsumer>>());
    }

    [Fact]
    public async Task DepartmentCreated_indexes_a_department_document()
    {
        var departmentId = Guid.NewGuid();
        var payload = $$"""{"DepartmentId":"{{departmentId}}","Name":"Marketing","Identifier":"marketing","ParentDepartmentId":null}""";
        var result = BuildResult(Guid.NewGuid(), "directory.events", departmentId.ToString(), "DepartmentCreated", payload);

        Assert.True(_sut.HandleWithRetryAndDeadLetter(result, CancellationToken.None));

        var doc = await _indexClient.GetAsync(SearchDocument.EntityId(SearchKind.Department, departmentId), CancellationToken.None);
        Assert.NotNull(doc);
        Assert.Equal("Marketing", doc!.Title);
        Assert.True(doc.IsActive);
    }

    [Fact]
    public async Task DepartmentDeleted_removes_the_department_document()
    {
        var departmentId = Guid.NewGuid();
        var createdPayload = $$"""{"DepartmentId":"{{departmentId}}","Name":"Temp","Identifier":"temp","ParentDepartmentId":null}""";
        Assert.True(_sut.HandleWithRetryAndDeadLetter(
            BuildResult(Guid.NewGuid(), "directory.events", departmentId.ToString(), "DepartmentCreated", createdPayload),
            CancellationToken.None));

        var deletedPayload = $$"""{"DepartmentId":"{{departmentId}}"}""";
        Assert.True(_sut.HandleWithRetryAndDeadLetter(
            BuildResult(Guid.NewGuid(), "directory.events", departmentId.ToString(), "DepartmentDeleted", deletedPayload),
            CancellationToken.None));

        var doc = await _indexClient.GetAsync(SearchDocument.EntityId(SearchKind.Department, departmentId), CancellationToken.None);
        Assert.Null(doc);
    }

    [Fact]
    public async Task DepartmentRenamed_reindexes_the_department_under_its_new_name_and_not_the_old_one()
    {
        var departmentId = Guid.NewGuid();
        Assert.True(_sut.HandleWithRetryAndDeadLetter(
            BuildResult(Guid.NewGuid(), "directory.events", departmentId.ToString(), "DepartmentCreated",
                $$"""{"DepartmentId":"{{departmentId}}","Name":"Payments","Identifier":"payments","ParentDepartmentId":null}"""),
            CancellationToken.None));

        Assert.True(_sut.HandleWithRetryAndDeadLetter(
            BuildResult(Guid.NewGuid(), "directory.events", departmentId.ToString(), "DepartmentRenamed",
                $$"""{"DepartmentId":"{{departmentId}}","Name":"Treasury operations","Identifier":"payments"}"""),
            CancellationToken.None));

        var doc = await _indexClient.GetAsync(SearchDocument.EntityId(SearchKind.Department, departmentId), CancellationToken.None);
        Assert.NotNull(doc);
        Assert.Equal("Treasury operations", doc!.Title);
        Assert.Equal("payments", doc.Subtitle);
        Assert.True(doc.IsActive);

        await _indexClient.RefreshAsync(CancellationToken.None);
        var byNewName = await _indexClient.SearchAsync("Treasury", [SearchKind.Department], 10, CancellationToken.None);
        Assert.Contains(byNewName, h => h.SourceId == departmentId);
        var byOldName = await _indexClient.SearchAsync("Payments", [SearchKind.Department], 10, CancellationToken.None);
        Assert.DoesNotContain(byOldName, h => h.SourceId == departmentId && h.Title == "Payments");
    }

    // The employee and position documents carry the department's name in their own subtitle, so a rename has to reach
    // them too. Seeded against a department that already has several people and a position, plus bystanders in
    // another department that must not change.
    [Fact]
    public async Task DepartmentRenamed_rewrites_the_name_on_the_employees_and_positions_of_that_department_only()
    {
        var department = Guid.NewGuid();
        var other = Guid.NewGuid();
        var engineer = Guid.NewGuid();
        var chen = Guid.NewGuid();
        var lee = Guid.NewGuid();
        var bystander = Guid.NewGuid();

        Send("directory.events", department, "DepartmentCreated",
            $$"""{"DepartmentId":"{{department}}","Name":"Payments","Identifier":"payments","ParentDepartmentId":null}""");
        Send("directory.events", other, "DepartmentCreated",
            $$"""{"DepartmentId":"{{other}}","Name":"Legal","Identifier":"legal","ParentDepartmentId":null}""");
        Send("directory.events", engineer, "PositionCreated",
            $$"""{"PositionId":"{{engineer}}","Name":"Engineer","Description":null,"DepartmentIds":["{{department}}"]}""");
        Hire(chen, "Maria Chen", "maria@example.com", department, engineer);
        Hire(lee, "Sam Lee", "sam@example.com", department, engineer);
        Hire(bystander, "Ana Ruiz", "ana@example.com", other, engineer);

        Send("directory.events", department, "DepartmentRenamed",
            $$"""{"DepartmentId":"{{department}}","Name":"Treasury operations","Identifier":"payments"}""");

        Assert.Equal("Engineer · Treasury operations", (await Doc(SearchKind.Employee, chen)).Subtitle);
        Assert.Equal("Engineer · Treasury operations", (await Doc(SearchKind.Employee, lee)).Subtitle);
        Assert.Equal("Engineer · Legal", (await Doc(SearchKind.Employee, bystander)).Subtitle);
        var position = await Doc(SearchKind.Position, engineer);
        Assert.Equal("Treasury operations", position.Subtitle);
        Assert.DoesNotContain("Payments", position.SearchText);

        // Everything else about the employee is kept, including the email the search text holds.
        Assert.Equal("Maria Chen maria@example.com", (await Doc(SearchKind.Employee, chen)).SearchText);
    }

    private void Send(string topic, Guid key, string type, string payload) =>
        Assert.True(_sut.HandleWithRetryAndDeadLetter(
            BuildResult(Guid.NewGuid(), topic, key.ToString(), type, payload), CancellationToken.None));

    private void Hire(Guid id, string name, string email, Guid department, Guid position) =>
        Send("employee.events", id, "EmployeeHired",
            $$"""{"EmployeeId":"{{id}}","FullName":"{{name}}","Email":"{{email}}","DepartmentId":"{{department}}","PositionId":"{{position}}"}""");

    private async Task<SearchDocument> Doc(string kind, Guid id) =>
        (await _indexClient.GetAsync(SearchDocument.EntityId(kind, id), CancellationToken.None))!;

    [Fact]
    public async Task EmployeeHired_resolves_department_and_position_names_into_the_subtitle()
    {
        var departmentId = Guid.NewGuid();
        var positionId = Guid.NewGuid();
        var employeeId = Guid.NewGuid();

        Assert.True(_sut.HandleWithRetryAndDeadLetter(
            BuildResult(Guid.NewGuid(), "directory.events", departmentId.ToString(), "DepartmentCreated",
                $$"""{"DepartmentId":"{{departmentId}}","Name":"Engineering","Identifier":"eng","ParentDepartmentId":null}"""),
            CancellationToken.None));
        Assert.True(_sut.HandleWithRetryAndDeadLetter(
            BuildResult(Guid.NewGuid(), "directory.events", positionId.ToString(), "PositionCreated",
                $$"""{"PositionId":"{{positionId}}","Name":"Engineer","Description":null,"DepartmentIds":["{{departmentId}}"]}"""),
            CancellationToken.None));

        var hiredPayload =
            $$"""{"EmployeeId":"{{employeeId}}","FullName":"Maria Chen","Email":"maria@example.com","DepartmentId":"{{departmentId}}","PositionId":"{{positionId}}"}""";
        Assert.True(_sut.HandleWithRetryAndDeadLetter(
            BuildResult(Guid.NewGuid(), "employee.events", employeeId.ToString(), "EmployeeHired", hiredPayload),
            CancellationToken.None));

        var doc = await _indexClient.GetAsync(SearchDocument.EntityId(SearchKind.Employee, employeeId), CancellationToken.None);
        Assert.NotNull(doc);
        Assert.Equal("Maria Chen", doc!.Title);
        Assert.Equal("Engineer · Engineering", doc.Subtitle);
        Assert.True(doc.IsActive);
    }

    [Fact]
    public async Task EmployeeTerminated_marks_the_document_inactive_instead_of_removing_it()
    {
        var employeeId = Guid.NewGuid();
        var hiredPayload =
            $$"""{"EmployeeId":"{{employeeId}}","FullName":"Sam Lee","Email":"sam@example.com","DepartmentId":"{{Guid.NewGuid()}}","PositionId":"{{Guid.NewGuid()}}"}""";
        Assert.True(_sut.HandleWithRetryAndDeadLetter(
            BuildResult(Guid.NewGuid(), "employee.events", employeeId.ToString(), "EmployeeHired", hiredPayload),
            CancellationToken.None));

        var terminatedPayload = $$"""{"EmployeeId":"{{employeeId}}"}""";
        Assert.True(_sut.HandleWithRetryAndDeadLetter(
            BuildResult(Guid.NewGuid(), "employee.events", employeeId.ToString(), "EmployeeTerminated", terminatedPayload),
            CancellationToken.None));

        var doc = await _indexClient.GetAsync(SearchDocument.EntityId(SearchKind.Employee, employeeId), CancellationToken.None);
        Assert.NotNull(doc);
        Assert.Equal("Sam Lee", doc!.Title);
        Assert.False(doc.IsActive);
    }

    [Fact]
    public async Task Every_message_also_becomes_an_audit_document()
    {
        var messageId = Guid.NewGuid();
        var departmentId = Guid.NewGuid();
        var payload = $$"""{"DepartmentId":"{{departmentId}}","Name":"Ops","Identifier":"ops","ParentDepartmentId":null}""";

        Assert.True(_sut.HandleWithRetryAndDeadLetter(
            BuildResult(messageId, "directory.events", departmentId.ToString(), "DepartmentCreated", payload),
            CancellationToken.None));

        var auditDoc = await _indexClient.GetAsync(messageId.ToString(), CancellationToken.None);
        Assert.NotNull(auditDoc);
        Assert.Equal(SearchKind.Audit, auditDoc!.Kind);
        Assert.Equal("DepartmentCreated", auditDoc.Title);
        Assert.Equal("directory", auditDoc.Subtitle);
    }

    [Fact]
    public async Task Redelivering_the_same_message_does_not_duplicate_the_audit_document()
    {
        var messageId = Guid.NewGuid();
        var departmentId = Guid.NewGuid();
        var payload = $$"""{"DepartmentId":"{{departmentId}}","Name":"Ops2","Identifier":"ops2","ParentDepartmentId":null}""";
        var result = BuildResult(messageId, "directory.events", departmentId.ToString(), "DepartmentCreated", payload);

        Assert.True(_sut.HandleWithRetryAndDeadLetter(result, CancellationToken.None));
        Assert.True(_sut.HandleWithRetryAndDeadLetter(result, CancellationToken.None));

        var auditDoc = await _indexClient.GetAsync(messageId.ToString(), CancellationToken.None);
        Assert.NotNull(auditDoc);
    }

    [Fact]
    public async Task Message_without_a_valid_message_id_header_is_skipped_without_error()
    {
        var result = new ConsumeResult<string, string>
        {
            Topic = "directory.events",
            Message = new Message<string, string> { Key = "dep-1", Value = "{}", Headers = new Headers() },
        };

        Assert.True(_sut.HandleWithRetryAndDeadLetter(result, CancellationToken.None));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _resetDatabase();

    private static ConsumeResult<string, string> BuildResult(
        Guid messageId, string topic, string key, string messageType, string payload)
    {
        var headers = new Headers
        {
            { "message-id", Encoding.UTF8.GetBytes(messageId.ToString()) },
            { "message-type", Encoding.UTF8.GetBytes(messageType) },
        };

        return new ConsumeResult<string, string>
        {
            Topic = topic,
            Message = new Message<string, string> { Key = key, Value = payload, Headers = headers },
        };
    }
}
