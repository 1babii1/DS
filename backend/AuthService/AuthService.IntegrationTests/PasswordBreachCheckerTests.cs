using System.Net;
using AuthService.Web.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AuthService.IntegrationTests;

// HaveIBeenPwnedPasswordChecker's own suffix-matching and fail-open logic, driven through a
// stubbed HttpMessageHandler rather than the real Pwned Passwords API - a live network
// dependency has no place in an automated test, same reasoning FakeEmailSender applies to
// SMTP. What's actually being proven here is that the k-anonymity response format ("SUFFIX:
// count" per line) is parsed correctly and that a third-party outage fails open, not closed.
public class PasswordBreachCheckerTests
{
    [Fact]
    public async Task A_password_whose_hash_suffix_is_in_the_response_is_reported_breached()
    {
        // SHA-1("password123") = CBFDAC6008F9CAB4083784CBD1874F76618D2A97
        // prefix "CBFDA", suffix "C6008F9CAB4083784CBD1874F76618D2A97"
        var checker = BuildChecker(HttpStatusCode.OK, "C6008F9CAB4083784CBD1874F76618D2A97:2000000\r\nOTHER00000000000000000000000000000:1");

        var result = await checker.IsBreachedAsync("password123", CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task A_password_whose_hash_suffix_is_absent_from_the_response_is_not_breached()
    {
        var checker = BuildChecker(HttpStatusCode.OK, "0000000000000000000000000000000000:1\r\nAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA:1");

        var result = await checker.IsBreachedAsync("a genuinely unique passphrase 9182", CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task An_unreachable_api_fails_open_rather_than_blocking_the_password()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("simulated outage"));
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.pwnedpasswords.com/") };
        var checker = new HaveIBeenPwnedPasswordChecker(client, NullLogger<HaveIBeenPwnedPasswordChecker>.Instance);

        var result = await checker.IsBreachedAsync("whatever", CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task A_non_success_response_fails_open_rather_than_blocking_the_password()
    {
        var checker = BuildChecker(HttpStatusCode.InternalServerError, string.Empty);

        var result = await checker.IsBreachedAsync("whatever", CancellationToken.None);

        Assert.False(result);
    }

    private static HaveIBeenPwnedPasswordChecker BuildChecker(HttpStatusCode statusCode, string body)
    {
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body),
        });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.pwnedpasswords.com/") };
        return new HaveIBeenPwnedPasswordChecker(client, NullLogger<HaveIBeenPwnedPasswordChecker>.Instance);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request, cancellationToken));
    }
}
