using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RewardsService.Domain;
using RewardsService.Infrastructure;

namespace RewardsService.IntegrationTests;

// The door to the anonymisation (ADR 0050): administrators with a fresh step-up only, and a grant to someone already erased is refused.
public class LedgerErasureEndpointTests(RewardsTestWebFactory factory) : IClassFixture<RewardsTestWebFactory>, IAsyncLifetime
{
    private const string Url = "/api/rewards/subjects/erase";

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => factory.ResetDatabaseAsync();

    [Fact]
    public async Task Without_a_token_a_non_admin_or_a_step_up_nothing_is_touched()
    {
        var employee = await SeedAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync(Url, new { subjects = new[] { employee.ToString() } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send("editor", elevated: true, employee)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send("admin", elevated: false, employee)).StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<RewardsDbContext>().Wallets.CountAsync(w => w.EmployeeId == employee));
    }

    [Fact]
    public async Task An_administrator_with_a_step_up_anonymises_and_a_later_grant_to_that_person_is_refused()
    {
        var employee = await SeedAsync();

        var response = await Send("admin", elevated: true, employee);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(1, body.GetProperty("walletsAnonymised").GetInt32());

        using var grant = new HttpRequestMessage(HttpMethod.Post, "/api/rewards/grants")
        {
            Content = JsonContent.Create(new { employeeId = employee, amount = 5, reason = "late" }),
        };
        grant.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        grant.Headers.Add(TestAuthHandler.RoleHeader, "admin");
        var granted = await factory.CreateClient().SendAsync(grant);
        Assert.Equal(HttpStatusCode.Conflict, granted.StatusCode);
    }

    [Fact]
    public async Task No_subjects_or_too_many_is_a_bad_request()
    {
        using var empty = Request(Array.Empty<string>());
        using var many = Request(Enumerable.Range(0, 51).Select(_ => Guid.NewGuid().ToString()).ToArray());

        Assert.Equal(HttpStatusCode.BadRequest, (await factory.CreateClient().SendAsync(empty)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await factory.CreateClient().SendAsync(many)).StatusCode);
    }

    private async Task<Guid> SeedAsync()
    {
        var employee = Guid.NewGuid();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RewardsDbContext>();
        new CurrencyGrantWriter(db).Grant(employee, 100, "seed", TransactionSource.WelcomeBonus, null);
        await db.SaveChangesAsync();
        return employee;
    }

    private async Task<HttpResponseMessage> Send(string role, bool elevated, Guid employee)
    {
        using var request = Request([employee.ToString()], role, elevated);
        return await factory.CreateClient().SendAsync(request);
    }

    private static HttpRequestMessage Request(string[] subjects, string role = "admin", bool elevated = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Url) { Content = JsonContent.Create(new { subjects }) };
        request.Headers.Add(TestAuthHandler.RoleHeader, role);
        if (elevated)
        {
            request.Headers.Add(TestAuthHandler.ElevatedHeader, "1");
        }

        return request;
    }
}
