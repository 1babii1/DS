using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace NotificationService.Web.HubTickets;

public sealed class HubTicketOptions
{
    public const string SectionName = "HubTickets";

    /// <summary>Base64 of 32+ random bytes. Required in Production; elsewhere an ephemeral key is used.</summary>
    public string? SigningKeyBase64 { get; set; }
}

public sealed record HubTicket(string Value, DateTimeOffset ExpiresAt);

/// <summary>
/// The credential a browser uses to open the notification hub. The browser never holds the OAuth access token (the
/// BFF keeps it server-side), and a WebSocket cannot send an Authorization header, so the BFF asks this service for a
/// ticket as the signed-in person and hands only that to the page. A ticket proves one thing: this account, for this
/// hub, for at most a minute. It is not a JWT, is signed with a key only this service holds and is accepted only by
/// the hub's own authentication scheme, so it cannot authenticate any REST endpoint, and an OAuth token cannot open
/// the hub.
/// </summary>
public sealed class HubTicketService
{
    public const string Audience = "notifications-hub";
    public const string Purpose = "hub-handshake";
    public const string Issuer = "notification-service";
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    // Clocks of the instances that issue and check a ticket may differ slightly.
    private static readonly TimeSpan Skew = TimeSpan.FromSeconds(5);

    private readonly byte[] _key;
    private readonly TimeProvider _clock;

    public HubTicketService(IOptions<HubTicketOptions> options, TimeProvider clock)
    {
        _key = Convert.FromBase64String(options.Value.SigningKeyBase64
            ?? throw new InvalidOperationException("HubTickets:SigningKeyBase64 is not set."));
        if (_key.Length < 32)
        {
            throw new InvalidOperationException("HubTickets:SigningKeyBase64 must decode to at least 32 bytes.");
        }

        _clock = clock;
    }

    public HubTicket Issue(Guid accountId)
    {
        var issuedAt = _clock.GetUtcNow();
        var expiresAt = issuedAt + Lifetime;
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Claims(
            accountId, Audience, Purpose, Issuer, issuedAt.ToUnixTimeSeconds(), expiresAt.ToUnixTimeSeconds())));

        return new HubTicket($"{payload}.{Base64Url(Sign(payload))}", DateTimeOffset.FromUnixTimeSeconds(expiresAt.ToUnixTimeSeconds()));
    }

    /// <summary>The account the ticket was issued to, or null for anything that is not a valid, current ticket.</summary>
    public Guid? Validate(string? ticket)
    {
        try
        {
            var parts = (ticket ?? string.Empty).Split('.');
            if (parts.Length != 2)
            {
                return null;
            }

            var signature = FromBase64Url(parts[1]);
            if (!CryptographicOperations.FixedTimeEquals(signature, Sign(parts[0])))
            {
                return null;
            }

            var claims = JsonSerializer.Deserialize<Claims>(FromBase64Url(parts[0]));
            if (claims is null
                || claims.Aud != Audience
                || claims.Pur != Purpose
                || claims.Iss != Issuer
                || claims.Sub == Guid.Empty)
            {
                return null;
            }

            var now = _clock.GetUtcNow();
            var issuedAt = DateTimeOffset.FromUnixTimeSeconds(claims.Iat);
            var expiresAt = DateTimeOffset.FromUnixTimeSeconds(claims.Exp);

            // The lifetime is checked on the ticket itself, so a signed ticket claiming an hour is still refused.
            if (expiresAt <= now || issuedAt > now + Skew || expiresAt - issuedAt > Lifetime)
            {
                return null;
            }

            return claims.Sub;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    private byte[] Sign(string payload) => HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes(payload));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        return Convert.FromBase64String(padded);
    }

    private sealed record Claims(
        [property: JsonPropertyName("sub")] Guid Sub,
        [property: JsonPropertyName("aud")] string Aud,
        [property: JsonPropertyName("pur")] string Pur,
        [property: JsonPropertyName("iss")] string Iss,
        [property: JsonPropertyName("iat")] long Iat,
        [property: JsonPropertyName("exp")] long Exp);
}
