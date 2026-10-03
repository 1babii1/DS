using System.Security.Claims;
using System.Text;
using System.Text.Json;
using AuditService.Domain;
using AuditService.Infrastructure;
using AuditService.Web.Controllers;
using Confluent.Kafka;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Security;

namespace AuditService.IntegrationTests;

// Personal fields in the audit log are stored under a key per subject and become unreadable when the subject is erased (ADR 0046).
// Messages go through the real consumer, rows are read back from the real table, and "unreadable" is checked on the stored bytes,
// not through the code that is supposed to hide them.
public class PiiErasureTests : IClassFixture<AuditTestWebFactory>, IAsyncLifetime
{
    private const string Email = "jane.doe@example.test";
    private const string Ip = "203.0.113.9";

    private readonly Func<Task> _resetDatabase;
    private readonly IServiceProvider _services;
    private readonly AuditConsumer _consumer;

    public PiiErasureTests(AuditTestWebFactory factory)
    {
        _services = factory.Services;
        _resetDatabase = factory.ResetDatabaseAsync;
        _consumer = new AuditConsumer(
            _services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new AuditConsumerOptions()),
            _services.GetRequiredService<ILogger<AuditConsumer>>());
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _resetDatabase();

    private static string Deleted(string subject) =>
        $$"""{"AccountId":"{{subject}}","Email":"{{Email}}","IpAddress":"{{Ip}}","OccurredAt":"2026-10-01T10:00:00Z"}""";

    private void Consume(string type, string key, string payload, string topic = "auth.events")
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

    private async Task<T> InDb<T>(Func<AuditDbContext, PiiVault, Task<T>> work)
    {
        await using var scope = _services.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<AuditDbContext>(), scope.ServiceProvider.GetRequiredService<PiiVault>());
    }

    private Task<string> StoredPayload(string key) =>
        InDb((db, _) => db.Entries.AsNoTracking().Where(e => e.AggregateId == key).Select(e => e.Payload).SingleAsync());

    [Fact]
    public async Task Personal_fields_are_not_stored_in_the_clear_and_are_readable_through_the_vault()
    {
        var subject = Guid.NewGuid().ToString();
        Consume("AccountDeleted", subject, Deleted(subject));

        var stored = await StoredPayload(subject);
        var revealed = await InDb((_, vault) => Task.FromResult(vault.Reveal(stored)));

        Assert.DoesNotContain(Email, stored, StringComparison.Ordinal);
        Assert.DoesNotContain(Ip, stored, StringComparison.Ordinal);
        Assert.Contains(subject, stored, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(revealed);
        Assert.Equal(Email, document.RootElement.GetProperty("Email").GetString());
        Assert.Equal(Ip, document.RootElement.GetProperty("IpAddress").GetString());
        Assert.Equal(subject, document.RootElement.GetProperty("AccountId").GetString());
    }

    [Fact]
    public async Task An_event_with_no_personal_fields_is_stored_exactly_as_it_arrived()
    {
        Consume("DepartmentCreated", "dep-1", """{"DepartmentId":"d","Name":"Engineering"}""", "directory.events");

        // The column is jsonb, which normalises spacing; what must hold is that nothing in it was touched.
        using var stored = JsonDocument.Parse(await StoredPayload("dep-1"));
        Assert.Equal("Engineering", stored.RootElement.GetProperty("Name").GetString());
        Assert.DoesNotContain("$pii", stored.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Erasing_a_subject_makes_its_fields_unreadable_and_leaves_what_happened()
    {
        var subject = Guid.NewGuid().ToString();
        Consume("AccountDeleted", subject, Deleted(subject));
        Consume("LoginSucceeded", subject, $$"""{"AccountId":"{{subject}}","Email":"{{Email}}","IpAddress":"{{Ip}}"}""");

        var newly = await InDb((_, vault) => vault.EraseAsync([subject], CancellationToken.None));

        Assert.Equal(1, newly);
        var entries = await InDb((db, vault) => Task.FromResult(db.Entries.AsNoTracking().Where(e => e.AggregateId == subject).ToList()));
        Assert.Equal(2, entries.Count);
        foreach (var entry in entries)
        {
            Assert.DoesNotContain(Email, entry.Payload, StringComparison.Ordinal);
            var revealed = await InDb((_, vault) => Task.FromResult(vault.Reveal(entry.Payload)));
            using var document = JsonDocument.Parse(revealed);
            Assert.Equal(PiiVault.ErasedMarker, document.RootElement.GetProperty("Email").GetString());
            Assert.Equal(PiiVault.ErasedMarker, document.RootElement.GetProperty("IpAddress").GetString());
            Assert.Equal(subject, document.RootElement.GetProperty("AccountId").GetString());
        }

        Assert.Contains(entries, e => e.EventType == "AccountDeleted");
        Assert.Contains(entries, e => e.EventType == "LoginSucceeded");
    }

    [Fact]
    public async Task The_key_is_gone_not_just_hidden()
    {
        var subject = Guid.NewGuid().ToString();
        Consume("AccountDeleted", subject, Deleted(subject));
        var before = await InDb((db, _) => db.SubjectKeys.AsNoTracking().SingleAsync(k => k.SubjectId == subject));
        Assert.Equal(60, before.WrappedKey.Length);

        await InDb((_, vault) => vault.EraseAsync([subject], CancellationToken.None));

        var after = await InDb((db, _) => db.SubjectKeys.AsNoTracking().SingleAsync(k => k.SubjectId == subject));
        Assert.Empty(after.WrappedKey);
        Assert.NotNull(after.ErasedAt);
    }

    [Fact]
    public async Task Erasing_one_subject_leaves_another_readable()
    {
        var gone = Guid.NewGuid().ToString();
        var kept = Guid.NewGuid().ToString();
        Consume("AccountDeleted", gone, Deleted(gone));
        Consume("AccountDeleted", kept, Deleted(kept));

        await InDb((_, vault) => vault.EraseAsync([gone], CancellationToken.None));

        var revealed = await InDb(async (_, vault) => vault.Reveal(await StoredPayload(kept)));
        Assert.Contains(Email, revealed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Erasing_twice_is_harmless_and_does_not_count_again()
    {
        var subject = Guid.NewGuid().ToString();
        Consume("AccountDeleted", subject, Deleted(subject));

        var first = await InDb((_, vault) => vault.EraseAsync([subject], CancellationToken.None));
        var second = await InDb((_, vault) => vault.EraseAsync([subject], CancellationToken.None));

        Assert.Equal(1, first);
        Assert.Equal(0, second);
    }

    [Fact]
    public async Task An_event_that_arrives_after_the_erasure_is_stored_redacted_and_gets_no_new_key()
    {
        var subject = Guid.NewGuid().ToString();
        await InDb((_, vault) => vault.EraseAsync([subject], CancellationToken.None));

        Consume("LoginSucceeded", subject, $$"""{"AccountId":"{{subject}}","Email":"{{Email}}","IpAddress":"{{Ip}}"}""");

        var stored = await StoredPayload(subject);
        Assert.DoesNotContain(Email, stored, StringComparison.Ordinal);
        Assert.Contains(PiiVault.ErasedMarker, stored, StringComparison.Ordinal);
        Assert.Empty((await InDb((db, _) => db.SubjectKeys.AsNoTracking().SingleAsync(k => k.SubjectId == subject))).WrappedKey);
    }

    [Fact]
    public async Task A_sealed_field_cannot_be_moved_to_another_field_or_another_subject()
    {
        var a = Guid.NewGuid().ToString();
        var b = Guid.NewGuid().ToString();
        Consume("AccountDeleted", a, Deleted(a));
        Consume("AccountDeleted", b, Deleted(b));
        var payloadA = System.Text.Json.Nodes.JsonNode.Parse(await StoredPayload(a))!.AsObject();
        var payloadB = System.Text.Json.Nodes.JsonNode.Parse(await StoredPayload(b))!.AsObject();

        // A's e-mail envelope placed in A's own IP field: bound to the field name, so it does not open there.
        var otherField = System.Text.Json.Nodes.JsonNode.Parse(payloadA.ToJsonString())!.AsObject();
        otherField["IpAddress"] = System.Text.Json.Nodes.JsonNode.Parse(payloadA["Email"]!.ToJsonString());
        // A's envelope with B named as its subject: the key is B's, which is not what it was sealed with.
        var otherSubject = System.Text.Json.Nodes.JsonNode.Parse(payloadA.ToJsonString())!.AsObject();
        otherSubject["Email"]!["s"] = b;
        _ = payloadB;

        await Assert.ThrowsAnyAsync<System.Security.Cryptography.CryptographicException>(
            () => InDb((_, vault) => Task.FromResult(vault.Reveal(otherField.ToJsonString()))));
        await Assert.ThrowsAnyAsync<System.Security.Cryptography.CryptographicException>(
            () => InDb((_, vault) => Task.FromResult(vault.Reveal(otherSubject.ToJsonString()))));
    }

    [Fact]
    public async Task Many_events_for_a_new_subject_at_once_all_end_up_under_one_key_and_readable()
    {
        var subject = Guid.NewGuid().ToString();
        using var barrier = new Barrier(8);
        var threads = Enumerable.Range(0, 8).Select(_ => new Thread(() =>
        {
            barrier.SignalAndWait();
            Consume("LoginSucceeded", subject, $$"""{"AccountId":"{{subject}}","Email":"{{Email}}","IpAddress":"{{Ip}}"}""");
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        Assert.Equal(1, await InDb((db, _) => db.SubjectKeys.CountAsync(k => k.SubjectId == subject)));
        var payloads = await InDb((db, _) => db.Entries.AsNoTracking().Where(e => e.AggregateId == subject).Select(e => e.Payload).ToListAsync());
        Assert.Equal(8, payloads.Count);
        await InDb((_, vault) =>
        {
            foreach (var payload in payloads)
            {
                Assert.Contains(Email, vault.Reveal(payload), StringComparison.Ordinal);
            }

            return Task.FromResult(0);
        });
    }

    [Fact]
    public async Task Without_a_master_key_the_vault_passes_payloads_through()
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var vault = TestVault.Disabled(db);

        Assert.False(vault.Enabled);
        Assert.Equal(Deleted("x"), vault.Protect("x", "AccountDeleted", Deleted("x")));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task The_org_history_still_builds_when_an_employee_has_been_erased()
    {
        var employee = Guid.NewGuid();
        var department = Guid.NewGuid();
        var position = Guid.NewGuid();
        Consume("DepartmentCreated", department.ToString(), $$"""{"DepartmentId":"{{department}}","Name":"Engineering","Identifier":"eng","ParentDepartmentId":null}""", "directory.events");
        Consume("PositionCreated", position.ToString(), $$"""{"PositionId":"{{position}}","Name":"Developer","Description":null,"DepartmentIds":["{{department}}"]}""", "directory.events");
        Consume("EmployeeHired", employee.ToString(), $$"""{"EmployeeId":"{{employee}}","FullName":"Anna Ivanova","Email":"anna@example.test","DepartmentId":"{{department}}","PositionId":"{{position}}"}""", "employee.events");
        await InDb((_, vault) => vault.EraseAsync([employee.ToString()], CancellationToken.None));

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var controller = new AuditController(db, scope.ServiceProvider.GetRequiredService<PiiVault>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, RoleNames.Viewer)], "t")) },
            },
        };
        var chart = await controller.OrgChart(null, CancellationToken.None);

        var people = chart.Value!.Departments.Single().People;
        Assert.Equal(PiiVault.ErasedMarker, Assert.Single(people).Name);
    }

    [Fact]
    public async Task Erasing_through_the_endpoint_is_recorded_without_naming_the_subject()
    {
        var subject = $"someone-{Guid.NewGuid():N}@example.test";
        Consume("LoginFailed", subject, $$"""{"Email":"{{subject}}","Reason":"bad password","IpAddress":"{{Ip}}"}""");
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var controller = new AuditController(db, scope.ServiceProvider.GetRequiredService<PiiVault>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "admin-1")], "t")) },
            },
        };

        var result = await controller.EraseSubjects(new EraseSubjectsRequest([subject]), CancellationToken.None);

        Assert.Equal(1, result.Value!.Newly);
        var record = await db.Entries.AsNoTracking().SingleAsync(e => e.EventType == "SubjectsErased");
        Assert.DoesNotContain(subject, record.Payload, StringComparison.Ordinal);
        Assert.Contains("subjectHashes", record.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Erasing_nobody_or_too_many_at_once_is_refused()
    {
        await using var scope = _services.CreateAsyncScope();
        var controller = new AuditController(
            scope.ServiceProvider.GetRequiredService<AuditDbContext>(), scope.ServiceProvider.GetRequiredService<PiiVault>());

        var none = await controller.EraseSubjects(new EraseSubjectsRequest([]), CancellationToken.None);
        var many = await controller.EraseSubjects(
            new EraseSubjectsRequest(Enumerable.Range(0, 51).Select(i => $"s{i}").ToArray()), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(none.Result);
        Assert.IsType<BadRequestObjectResult>(many.Result);
    }

    [Fact]
    public void The_fields_the_audit_log_encrypts_are_exactly_the_ones_the_personal_data_inventory_declares()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "backend.slnx")))
        {
            directory = directory.Parent;
        }

        var inventory = Path.Combine(directory!.FullName, "..", "docs", "security", "pii-inventory.json");
        using var document = JsonDocument.Parse(File.ReadAllText(inventory));
        var declared = document.RootElement.GetProperty("pii").EnumerateArray()
            .Select(e => $"{e.GetProperty("event").GetString()}.{e.GetProperty("field").GetString()}")
            .Order().ToList();
        var catalog = PiiCatalog.FieldsByEvent.SelectMany(e => e.Value.Select(f => $"{e.Key}.{f}")).Order().ToList();

        Assert.Equal(declared, catalog);
    }
}
