using System.Collections.Concurrent;
using AuthService.Web.Configuration;

namespace AuthService.IntegrationTests.Infrastructure;

// Swapped in for SmtpEmailSender in AuthTestWebFactory - integration tests run
// against a real Postgres via Testcontainers, but there's no real SMTP server to
// connect to, and tests asserting the Hire Employee saga's outcome (or the
// register/confirm/reset-password flows) need to see what "was sent" without an
// actual mailbox - the confirmation/reset link is the only way a test can drive
// those flows end-to-end.
public class FakeEmailSender : IEmailSender
{
    private readonly ConcurrentBag<(string ToEmail, string Password)> _sent = [];
    private readonly ConcurrentBag<(string ToEmail, string Link)> _confirmations = [];
    private readonly ConcurrentBag<(string ToEmail, string Link)> _resets = [];
    private readonly ConcurrentBag<(string ToEmail, string Code)> _stepUpCodes = [];
    private readonly ConcurrentBag<(string ToEmail, string IpAddress)> _newSignInNotifications = [];
    private readonly ConcurrentBag<(string ToEmail, string Link)> _emailChangeConfirmations = [];
    private readonly ConcurrentBag<string> _duplicateRegistrationNotices = [];

    public IReadOnlyCollection<(string ToEmail, string Password)> Sent => _sent;

    public IReadOnlyCollection<(string ToEmail, string Link)> Confirmations => _confirmations;

    public IReadOnlyCollection<(string ToEmail, string Link)> Resets => _resets;

    public IReadOnlyCollection<(string ToEmail, string Code)> StepUpCodes => _stepUpCodes;

    public IReadOnlyCollection<(string ToEmail, string IpAddress)> NewSignInNotifications => _newSignInNotifications;

    public IReadOnlyCollection<(string ToEmail, string Link)> EmailChangeConfirmations => _emailChangeConfirmations;

    public IReadOnlyCollection<string> DuplicateRegistrationNotices => _duplicateRegistrationNotices;

    public Task SendNewAccountPasswordAsync(string toEmail, string temporaryPassword, CancellationToken cancellationToken)
    {
        _sent.Add((toEmail, temporaryPassword));
        return Task.CompletedTask;
    }

    public Task SendEmailConfirmationAsync(string toEmail, string confirmationLink, CancellationToken cancellationToken)
    {
        _confirmations.Add((toEmail, confirmationLink));
        return Task.CompletedTask;
    }

    public Task SendPasswordResetAsync(string toEmail, string resetLink, CancellationToken cancellationToken)
    {
        _resets.Add((toEmail, resetLink));
        return Task.CompletedTask;
    }

    public Task SendStepUpCodeAsync(string toEmail, string code, CancellationToken cancellationToken)
    {
        _stepUpCodes.Add((toEmail, code));
        return Task.CompletedTask;
    }

    public Task SendNewSignInNotificationAsync(
        string toEmail, string ipAddress, DateTime occurredAt, CancellationToken cancellationToken)
    {
        _newSignInNotifications.Add((toEmail, ipAddress));
        return Task.CompletedTask;
    }

    public Task SendEmailChangeConfirmationAsync(string toEmail, string confirmationLink, CancellationToken cancellationToken)
    {
        _emailChangeConfirmations.Add((toEmail, confirmationLink));
        return Task.CompletedTask;
    }

    public Task SendDuplicateRegistrationNoticeAsync(string toEmail, CancellationToken cancellationToken)
    {
        _duplicateRegistrationNotices.Add(toEmail);
        return Task.CompletedTask;
    }
}
