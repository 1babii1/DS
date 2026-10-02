using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NotificationService.Web.HubTickets;

namespace NotificationService.IntegrationTests;

// The handshake and the REST surface through the real pipeline: who may open the hub, and that the credential for one
// is useless on the other. SignalR's negotiate request goes through the same authentication as the WebSocket that
// follows it, so a 200 or 401 there is the handshake's verdict.
public class HubAuthenticationTests : IClassFixture<NotificationTestWebFactory>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HubAuthenticationTests(NotificationTestWebFactory factory) =>
        _factory = factory.WithWebHostBuilder(builder => builder.UseSetting(
            "HubTickets:SigningKeyBase64", Convert.ToBase64String(HubTicketServiceTests.Key)));

    private HubTicketService Tickets => _factory.Services.GetRequiredService<HubTicketService>();

    private async Task<HttpResponseMessage> Negotiate(string? credentialInQuery)
    {
        using var client = _factory.CreateClient();
        var query = credentialInQuery is null ? string.Empty : $"&access_token={Uri.EscapeDataString(credentialInQuery)}";
        return await client.PostAsync($"/hub/notifications/negotiate?negotiateVersion=1{query}", content: null);
    }

    [Fact]
    public async Task A_valid_ticket_opens_the_hub()
    {
        var ticket = Tickets.Issue(Guid.NewGuid());

        Assert.Equal(HttpStatusCode.OK, (await Negotiate(ticket.Value)).StatusCode);
    }

    [Fact]
    public async Task No_ticket_does_not_open_the_hub()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Negotiate(null)).StatusCode);
    }

    [Fact]
    public async Task An_expired_wrong_audience_forged_or_malformed_ticket_does_not_open_the_hub()
    {
        var sub = Guid.NewGuid();
        var expired = HubTicketServiceTests.Mint(HubTicketServiceTests.Claims(sub, DateTimeOffset.UtcNow.AddMinutes(-5)));
        var wrongAudience = HubTicketServiceTests.Mint(HubTicketServiceTests.Claims(sub, DateTimeOffset.UtcNow, aud: "rewards-api"));
        var forged = HubTicketServiceTests.Mint(
            HubTicketServiceTests.Claims(sub, DateTimeOffset.UtcNow), System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

        foreach (var bad in new[] { expired, wrongAudience, forged, "not-a-ticket", "a.b" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await Negotiate(bad)).StatusCode);
        }
    }

    [Fact]
    public async Task An_oauth_style_bearer_token_in_the_query_no_longer_opens_the_hub()
    {
        // Looks like what the hub used to accept: a three-part JWT. It is not a ticket.
        const string jwtShaped = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiI4ZTY0In0.c2lnbmF0dXJl";

        Assert.Equal(HttpStatusCode.Unauthorized, (await Negotiate(jwtShaped)).StatusCode);
    }

    [Fact]
    public async Task A_ticket_does_not_authenticate_the_rest_endpoints_in_the_header_or_the_query()
    {
        var ticket = Tickets.Issue(Guid.NewGuid()).Value;
        using var client = _factory.CreateClient();

        foreach (var path in new[] { "/api/notifications", "/api/notifications/unread-count" })
        {
            using var inHeader = new HttpRequestMessage(HttpMethod.Get, path);
            inHeader.Headers.Authorization = new("Bearer", ticket);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(inHeader)).StatusCode);

            var inQuery = await client.GetAsync($"{path}?access_token={Uri.EscapeDataString(ticket)}");
            Assert.Equal(HttpStatusCode.Unauthorized, inQuery.StatusCode);
        }

        using var read = new HttpRequestMessage(HttpMethod.Post, "/api/notifications/read-all");
        read.Headers.Authorization = new("Bearer", ticket);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(read)).StatusCode);
    }

    [Fact]
    public async Task Asking_for_a_ticket_without_signing_in_is_refused()
    {
        using var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/notifications/hub-ticket", content: null)).StatusCode);
    }

    [Fact]
    public void The_ticket_lifetime_is_at_most_a_minute()
    {
        Assert.True(HubTicketService.Lifetime <= TimeSpan.FromSeconds(60));
    }

    // The ticket travels in the query string, and ASP.NET Core's own "Request starting ..." log line prints the query
    // string. That line is logged at Information by the Microsoft.AspNetCore.Hosting category, so the platform's
    // Serilog configuration must keep every "Microsoft" category above it. The request log of Serilog itself writes
    // the path only. (Telemetry is covered by Shared.UnitTests: the span redacts query values.)
    [Theory]
    [InlineData("Development")]
    [InlineData("Docker")]
    [InlineData("Production")]
    public void Request_logs_that_would_print_the_query_string_are_not_enabled(string environment)
    {
        var root = _factory.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>().ContentRootPath;
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root, $"appsettings.{environment}.json")));
        var level = document.RootElement.GetProperty("Serilog").GetProperty("MinimumLevel").GetProperty("Override").GetProperty("Microsoft").GetString();

        Assert.Equal("Warning", level);
    }
}
