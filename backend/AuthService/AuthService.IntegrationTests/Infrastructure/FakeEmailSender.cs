using System.Collections.Concurrent;
using AuthService.Web.Configuration;

namespace AuthService.IntegrationTests.Infrastructure;

// Swapped in for SmtpEmailSender in AuthTestWebFactory - integration tests run
// against a real Postgres via Testcontainers, but there's no real SMTP server to
// connect to, and tests asserting the Hire Employee saga's outcome need to see
// what "was sent" without an actual mailbox.
public class FakeEmailSender : IEmailSender
{
    private readonly ConcurrentBag<(string ToEmail, string Password)> _sent = [];

    public IReadOnlyCollection<(string ToEmail, string Password)> Sent => _sent;

    public Task SendNewAccountPasswordAsync(string toEmail, string temporaryPassword, CancellationToken cancellationToken)
    {
        _sent.Add((toEmail, temporaryPassword));
        return Task.CompletedTask;
    }
}
