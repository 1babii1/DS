using System.Security.Claims;
using System.Text.Json;
using AuditService.Domain;
using AuditService.Infrastructure;
using AuditService.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shared.Security;

namespace AuditService.IntegrationTests;

// The org at a past instant, over the real log in a real database. Called directly like the other controller tests;
// [Authorize] is framework middleware. What is tested is what this code does: which events it reads, how it reads
// the date, what it refuses, and that a snapshot carries names and positions and no contact detail.
public class OrgChartEndpointTests : IClassFixture<AuditTestWebFactory>, IAsyncLifetime
{
    private static readonly Guid Eng = Guid.NewGuid();
    private static readonly Guid Developer = Guid.NewGuid();
    private static readonly Guid Anna = Guid.NewGuid();

    private readonly Func<Task> _resetDatabase;
    private readonly IServiceProvider _services;

    public OrgChartEndpointTests(AuditTestWebFactory factory)
    {
        _services = factory.Services;
        _resetDatabase = factory.ResetDatabaseAsync;
    }

    private static DateTime D(int month, int day, int hour = 12, int minute = 0) =>
        new(2026, month, day, hour, minute, 0, DateTimeKind.Utc);

    [Fact]
    public async Task The_snapshot_is_the_org_folded_from_the_log_up_to_the_asked_instant()
    {
        await SeedHistory();

        var early = await Get(D(1, 3).ToString("O"));
        var later = await Get(D(1, 8).ToString("O"));

        Assert.Equal(["Engineering"], early.Departments.Select(d => d.Name).ToArray());
        Assert.Empty(early.Departments.Single().People);
        Assert.Equal(["Anna Ivanova"], later.Departments.Single().People.Select(p => p.Name).ToArray());
        Assert.Equal("Developer", later.Departments.Single().People.Single().Position);
    }

    [Fact]
    public async Task A_bare_date_means_the_end_of_that_utc_day()
    {
        await SeedHistory(hireAt: D(1, 5, 23, 30));

        var thatDay = await Get("2026-01-05");
        var dayBefore = await Get("2026-01-04");

        Assert.Single(thatDay.Departments.Single().People);
        Assert.Empty(dayBefore.Departments.Single().People);
    }

    [Fact]
    public async Task A_time_in_the_future_is_the_present_and_no_time_at_all_is_now()
    {
        await SeedHistory();

        var future = await Get("2999-01-01");
        var none = await Get(null);

        Assert.True(future.At <= DateTime.UtcNow.AddSeconds(5));
        Assert.Single(future.Departments.Single().People);
        Assert.Single(none.Departments.Single().People);
    }

    [Fact]
    public async Task The_first_recorded_event_is_reported_so_a_slider_knows_where_history_starts()
    {
        await SeedHistory();

        var chart = await Get(null);

        Assert.Equal(D(1, 1), chart.FirstEventAt);
    }

    [Theory]
    [InlineData("yesterday")]
    [InlineData("2026-13-45")]
    [InlineData("0001-01-01")]
    [InlineData("1999-12-31")]
    public async Task A_time_that_is_not_a_time_or_is_absurd_is_refused(string at)
    {
        var (status, _) = await Call(at);

        Assert.Equal(StatusCodes.Status400BadRequest, status);
    }

    [Fact]
    public async Task A_history_too_large_to_replay_is_refused_rather_than_partly_answered()
    {
        await SeedHistory();

        var (status, _) = await Call(null, maxEvents: 2);

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, status);
    }

    [Fact]
    public async Task Events_the_fold_does_not_read_do_not_count_toward_that_limit()
    {
        await SeedHistory();
        Seed("CurrencyGranted", "x", """{"EmployeeId":"11111111-1111-1111-1111-111111111111"}""", D(1, 6));
        Seed("CurrencyGranted", "y", """{"EmployeeId":"11111111-1111-1111-1111-111111111111"}""", D(1, 7));

        var (status, _) = await Call(null, maxEvents: 4);

        Assert.Equal(StatusCodes.Status200OK, status);
    }

    [Fact]
    public async Task Any_signed_in_user_may_read_it_and_the_answer_holds_no_email_while_the_raw_feed_stays_admin_only()
    {
        await SeedHistory();

        var (status, body) = await Call(null, User(RoleNames.Viewer));

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Contains("Anna Ivanova", body);
        Assert.DoesNotContain("secret.example", body);
        Assert.DoesNotContain("@", body);

        var feed = await new AuditController(Db())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = User(RoleNames.Viewer) } },
        }.List(null, null, null, CancellationToken.None);
        Assert.Null(feed.Value!.Items.First(e => e.EventType == "EmployeeHired").Payload);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _resetDatabase();

    private async Task SeedHistory(DateTime? hireAt = null)
    {
        Seed("DepartmentCreated", Eng.ToString(),
            $$"""{"DepartmentId":"{{Eng}}","Name":"Engineering","Identifier":"eng","ParentDepartmentId":null}""", D(1, 1));
        Seed("PositionCreated", Developer.ToString(),
            $$"""{"PositionId":"{{Developer}}","Name":"Developer","Description":null,"DepartmentIds":["{{Eng}}"]}""", D(1, 1));
        Seed("EmployeeHired", Anna.ToString(),
            $$"""{"EmployeeId":"{{Anna}}","FullName":"Anna Ivanova","Email":"anna@secret.example","DepartmentId":"{{Eng}}","PositionId":"{{Developer}}"}""",
            hireAt ?? D(1, 5));
        await Task.CompletedTask;
    }

    private void Seed(string type, string aggregateId, string payload, DateTime occurredAt)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        db.Entries.Add(AuditEntry.Create(Guid.NewGuid(), "directory", type, aggregateId, payload, occurredAt));
        db.SaveChanges();
    }

    private AuditDbContext Db() => _services.CreateScope().ServiceProvider.GetRequiredService<AuditDbContext>();

    private async Task<(int Status, string Body)> Call(string? at, ClaimsPrincipal? user = null, int maxEvents = 50_000)
    {
        await using var scope = _services.CreateAsyncScope();
        var controller = new AuditController(
            scope.ServiceProvider.GetRequiredService<AuditDbContext>(),
            Options.Create(new OrgChartOptions { MaxEvents = maxEvents }))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user ?? User(RoleNames.Viewer) } },
        };

        var result = await controller.OrgChart(at, CancellationToken.None);
        return result.Result switch
        {
            ObjectResult o => (o.StatusCode ?? StatusCodes.Status200OK, JsonSerializer.Serialize(o.Value)),
            _ => (StatusCodes.Status200OK, JsonSerializer.Serialize(result.Value)),
        };
    }

    private async Task<OrgChartResponse> Get(string? at)
    {
        var (status, body) = await Call(at);
        Assert.Equal(StatusCodes.Status200OK, status);
        return JsonSerializer.Deserialize<OrgChartResponse>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private static ClaimsPrincipal User(string role) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, role)], "test"));
}
