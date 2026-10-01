using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NotificationService.Web.HubTickets;

namespace NotificationService.IntegrationTests;

// The ticket is what a browser holds to open the notification hub: it never sees the OAuth token. So what matters is
// that it proves exactly one thing (this account, for this hub, for a minute) and nothing else.
public class HubTicketServiceTests
{
    internal static readonly byte[] Key = Encoding.UTF8.GetBytes("test-only-hub-ticket-key-32bytes!");

    private static readonly DateTimeOffset Start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static HubTicketService Service(FixedClock clock, byte[]? key = null) =>
        new(Options.Create(new HubTicketOptions { SigningKeyBase64 = Convert.ToBase64String(key ?? Key) }), clock);

    // Mints a ticket the way the service does, but with any claims, so the checks on the other side can be tried.
    internal static string Mint(object claims, byte[]? key = null)
    {
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(claims));
        var signature = Base64Url(HMACSHA256.HashData(key ?? Key, Encoding.ASCII.GetBytes(payload)));
        return $"{payload}.{signature}";
    }

    internal static object Claims(
        Guid sub,
        DateTimeOffset iat,
        int lifetimeSeconds = 60,
        string aud = "notifications-hub",
        string pur = "hub-handshake",
        string iss = "notification-service") =>
        new { sub, aud, pur, iss, iat = iat.ToUnixTimeSeconds(), exp = iat.AddSeconds(lifetimeSeconds).ToUnixTimeSeconds() };

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public void A_ticket_it_issued_validates_as_that_account_and_lives_at_most_a_minute()
    {
        var service = Service(new FixedClock(Start));
        var account = Guid.NewGuid();

        var ticket = service.Issue(account);

        Assert.Equal(account, service.Validate(ticket.Value));
        Assert.Equal(Start.AddSeconds(60), ticket.ExpiresAt);
    }

    [Fact]
    public void A_ticket_is_one_account_and_not_another()
    {
        var service = Service(new FixedClock(Start));
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();

        Assert.NotEqual(theirs, service.Validate(service.Issue(mine).Value));
    }

    [Fact]
    public void An_expired_ticket_is_rejected()
    {
        var clock = new FixedClock(Start);
        var service = Service(clock);
        var ticket = service.Issue(Guid.NewGuid());

        clock.Now = Start.AddSeconds(60);

        Assert.Null(service.Validate(ticket.Value));
    }

    [Fact]
    public void A_ticket_for_another_audience_or_purpose_or_issuer_is_rejected_even_when_signed_by_the_key()
    {
        var service = Service(new FixedClock(Start));
        var sub = Guid.NewGuid();

        Assert.Equal(sub, service.Validate(Mint(Claims(sub, Start))));
        Assert.Null(service.Validate(Mint(Claims(sub, Start, aud: "rewards-api"))));
        Assert.Null(service.Validate(Mint(Claims(sub, Start, pur: "api-access"))));
        Assert.Null(service.Validate(Mint(Claims(sub, Start, iss: "auth-service"))));
    }

    [Fact]
    public void A_signed_ticket_that_claims_to_live_longer_than_a_minute_is_rejected()
    {
        var service = Service(new FixedClock(Start));

        Assert.Null(service.Validate(Mint(Claims(Guid.NewGuid(), Start, lifetimeSeconds: 3600))));
    }

    [Fact]
    public void A_ticket_from_the_future_is_rejected()
    {
        var service = Service(new FixedClock(Start));

        Assert.Null(service.Validate(Mint(Claims(Guid.NewGuid(), Start.AddMinutes(10)))));
    }

    [Fact]
    public void A_ticket_signed_with_another_key_is_rejected()
    {
        var service = Service(new FixedClock(Start));
        var forged = Mint(Claims(Guid.NewGuid(), Start), RandomNumberGenerator.GetBytes(32));

        Assert.Null(service.Validate(forged));
    }

    [Fact]
    public void A_changed_payload_is_rejected()
    {
        var service = Service(new FixedClock(Start));
        var original = service.Issue(Guid.NewGuid()).Value;
        var swapped = Mint(Claims(Guid.NewGuid(), Start)).Split('.')[0] + "." + original.Split('.')[1];

        Assert.Null(service.Validate(swapped));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("a.b")]
    [InlineData("a.b.c")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ4In0.sig")]
    [InlineData("!!!.???")]
    public void Malformed_input_is_rejected_not_thrown(string ticket)
    {
        Assert.Null(Service(new FixedClock(Start)).Validate(ticket));
    }
}

internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
