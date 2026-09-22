using System.Security.Cryptography;
using System.Text;

namespace AuthService.Web.Configuration;

public interface IPasswordBreachChecker
{
    /// <returns>True if the password is known to appear in a public breach corpus. False
    /// both for "not found" and for "could not check right now" - the caller cannot and
    /// should not distinguish those two, see HaveIBeenPwnedPasswordChecker's own reasoning.</returns>
    Task<bool> IsBreachedAsync(string password, CancellationToken cancellationToken);
}

/// <summary>
/// Have I Been Pwned's Pwned Passwords API, via k-anonymity: only the first 5 hex characters
/// of the password's SHA-1 hash ever leave this service - HIBP returns every suffix sharing
/// that prefix (typically several hundred), and the actual match happens locally. The full
/// password, and even its full hash, is never transmitted or logged.
/// </summary>
public class HaveIBeenPwnedPasswordChecker(HttpClient httpClient, ILogger<HaveIBeenPwnedPasswordChecker> logger)
    : IPasswordBreachChecker
{
    public async Task<bool> IsBreachedAsync(string password, CancellationToken cancellationToken)
    {
#pragma warning disable CA5350 // The Pwned Passwords API protocol itself is defined around SHA-1 - not a choice made here.
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(password)));
#pragma warning restore CA5350
        var prefix = hash[..5];
        var suffix = hash[5..];

        try
        {
            using var response = await httpClient.GetAsync($"range/{prefix}", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            foreach (var line in body.AsSpan().EnumerateLines())
            {
                var separator = line.IndexOf(':');
                if (separator > 0 && line[..separator].Equals(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException || IsPollyRejection(ex))
        {
            // Fail open: a third-party outage must not block registration or password
            // resets. This check is defense-in-depth on top of the length/complexity rules
            // Identity already enforces, not the only gate - losing it temporarily is an
            // acceptable, visible-in-logs degradation, not a security hole. The resilience
            // handler wrapping this HttpClient can itself throw BrokenCircuitException
            // (breaker open) or TimeoutRejectedException, neither of which is an
            // HttpRequestException - both must fail open the same way a raw network error does.
            logger.LogWarning(ex, "Could not reach the Pwned Passwords API - skipping the breach check for this password.");
            return false;
        }
    }

    // OpenIddict.Client/Validation.SystemNetHttp transitively pull in the legacy Polly 7
    // package alongside Polly.Core 8 (the one Microsoft.Extensions.Http.Resilience actually
    // uses) - both assemblies declare Polly.CircuitBreaker.BrokenCircuitException and
    // Polly.Timeout.TimeoutRejectedException under identical namespaces, so a direct type
    // reference to either is ambiguous (CS0433) in this project specifically. Matching by
    // name sidesteps the assembly conflict instead of pulling in an extern alias just for
    // two exception types.
    private static bool IsPollyRejection(Exception ex) =>
        ex.GetType() is { Namespace: "Polly.CircuitBreaker" or "Polly.Timeout" } type
        && type.Name is "BrokenCircuitException" or "TimeoutRejectedException";
}
