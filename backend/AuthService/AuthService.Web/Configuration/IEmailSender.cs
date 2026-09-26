namespace AuthService.Web.Configuration;

public interface IEmailSender
{
    Task SendNewAccountPasswordAsync(string toEmail, string temporaryPassword, CancellationToken cancellationToken);

    Task SendEmailConfirmationAsync(string toEmail, string confirmationLink, CancellationToken cancellationToken);

    Task SendPasswordResetAsync(string toEmail, string resetLink, CancellationToken cancellationToken);

    Task SendStepUpCodeAsync(string toEmail, string code, CancellationToken cancellationToken);

    Task SendNewSignInNotificationAsync(
        string toEmail, string ipAddress, DateTime occurredAt, CancellationToken cancellationToken);

    Task SendEmailChangeConfirmationAsync(string toEmail, string confirmationLink, CancellationToken cancellationToken);

    /// <summary>Sent instead of a confirmation email when /auth/register targets an address that
    /// already has an account - see AccountRecoveryService.CompleteRegistrationAsync for why.</summary>
    Task SendDuplicateRegistrationNoticeAsync(string toEmail, CancellationToken cancellationToken);
}
