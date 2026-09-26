using System.Security.Claims;
using System.Text;
using AuditService.Infrastructure;
using AuditService.Web.Controllers;
using Confluent.Kafka;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared;
using Shared.Security;

namespace AuditService.IntegrationTests;

// Calls the controller directly rather than over HTTP, the same seam-level approach used
// by AuditConsumerTests next door. [Authorize] itself is framework middleware and is not
// re-derived here; what IS tested is the payload gating the controller does in its own
// code, because the audit log is readable by every authenticated user - including a
// self-registered one - while Payload carries raw event bodies (employee names, emails).
public class AuditControllerTests : IClassFixture<AuditTestWebFactory>, IAsyncLifetime
{
    private const string EmployeePayload =
        """{"EmployeeId":"11111111-1111-1111-1111-111111111111","FullName":"Maria Chen","Email":"maria@example.com"}""";

    private readonly Func<Task> _resetDatabase;
    private readonly IServiceProvider _services;
    private readonly AuditConsumer _consumer;

    public AuditControllerTests(AuditTestWebFactory factory)
    {
        _services = factory.Services;
        _resetDatabase = factory.ResetDatabaseAsync;

        _consumer = new AuditConsumer(
            _services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new AuditConsumerOptions()),
            _services.GetRequiredService<ILogger<AuditConsumer>>());
    }

    [Fact]
    public async Task Admin_sees_the_raw_event_payload()
    {
        SeedEntry("emp-1", "EmployeeHired", EmployeePayload);

        var page = await List(Admin());

        // Compared by content, not as a string: the column is jsonb, so Postgres returns
        // the document normalised (keys reordered, whitespace rewritten) rather than byte
        // -identical to what was produced.
        var entry = Assert.Single(page.Items);
        Assert.NotNull(entry.Payload);
        Assert.Contains("maria@example.com", entry.Payload);
        Assert.Contains("Maria Chen", entry.Payload);
    }

    [Theory]
    [InlineData(RoleNames.Viewer)]
    [InlineData(RoleNames.Editor)]
    public async Task Non_admin_never_receives_the_raw_event_payload(string role)
    {
        SeedEntry("emp-1", "EmployeeHired", EmployeePayload);

        var page = await List(User(role));

        var entry = Assert.Single(page.Items);
        Assert.Null(entry.Payload);
    }

    [Fact]
    public async Task Non_admin_still_sees_everything_the_activity_feed_renders()
    {
        SeedEntry("emp-1", "EmployeeHired", EmployeePayload);

        var entry = Assert.Single((await List(User(RoleNames.Viewer))).Items);

        // Gating the payload must not gut the feed itself - these are the fields the UI uses.
        Assert.Equal("employee", entry.SourceService);
        Assert.Equal("EmployeeHired", entry.EventType);
        Assert.Equal("emp-1", entry.AggregateId);
        Assert.NotEqual(default, entry.OccurredAt);
    }

    [Fact]
    public async Task Entries_can_be_filtered_by_aggregate_id()
    {
        SeedEntry("emp-1", "EmployeeHired", "{}");
        SeedEntry("emp-2", "EmployeeHired", "{}");

        var page = await List(Admin(), aggregateId: "emp-2");

        Assert.Equal("emp-2", Assert.Single(page.Items).AggregateId);
    }

    [Fact]
    public async Task An_empty_log_is_an_empty_page_not_an_error()
    {
        var page = await List(Admin());

        Assert.Empty(page.Items);
        Assert.Equal(0, page.Total);
    }

    [Theory]
    [InlineData(null, PagedResponse<AuditEntryDto>.DefaultSize)]
    [InlineData(0, 1)]
    [InlineData(int.MaxValue, PagedResponse<AuditEntryDto>.MaxSize)]
    public async Task Page_size_is_clamped_rather_than_trusted(int? requested, int expected)
    {
        SeedEntry("emp-1", "EmployeeHired", "{}");

        var page = await List(Admin(), pageSize: requested);

        Assert.Equal(expected, page.Size);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _resetDatabase();

    private void SeedEntry(string aggregateId, string eventType, string payload)
    {
        var headers = new Headers
        {
            { "message-id", Encoding.UTF8.GetBytes(Guid.NewGuid().ToString()) },
            { "message-type", Encoding.UTF8.GetBytes(eventType) },
        };

        var result = new ConsumeResult<string, string>
        {
            Topic = "employee.events",
            Message = new Message<string, string> { Key = aggregateId, Value = payload, Headers = headers },
        };

        Assert.True(_consumer.HandleWithRetryAndDeadLetter(result, CancellationToken.None));
    }

    private async Task<PagedResponse<AuditEntryDto>> List(
        ClaimsPrincipal user, string? aggregateId = null, int? pageSize = null)
    {
        await using var scope = _services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var controller = new AuditController(dbContext)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = user },
            },
        };

        var response = await controller.List(aggregateId, page: null, pageSize, CancellationToken.None);
        return response.Value ?? throw new InvalidOperationException("Expected a page of audit entries");
    }

    private static ClaimsPrincipal Admin() => User(RoleNames.Admin);

    // "role" mirrors RoleClaimType as configured on the JWT handler in Program.cs - an
    // identity built with the default claim type would report IsInRole false for everyone.
    private static ClaimsPrincipal User(string role) =>
        new(new ClaimsIdentity([new Claim("role", role)], "test", ClaimTypes.Name, "role"));
}
