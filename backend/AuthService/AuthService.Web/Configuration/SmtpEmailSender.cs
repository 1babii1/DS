using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace AuthService.Web.Configuration;

// SendNewAccountPasswordAsync is used by EmployeeEventsConsumer to hand a newly hired
// employee their generated password. A failed send there is logged but never fails the
// provisioning step itself: the account already exists by the time it runs, so a delivery
// failure is an ops problem to retry manually, not a reason to compensate the whole saga.
// SendEmailConfirmationAsync/SendPasswordResetAsync back the self-service register/forgot-
// password flows in AccountController and the matching Razor pages.
public class SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    private readonly EmailOptions _options = options.Value;

    public async Task SendNewAccountPasswordAsync(
        string toEmail, string temporaryPassword, CancellationToken cancellationToken)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_options.FromName, _options.FromAddress));
        message.To.Add(MailboxAddress.Parse(toEmail));
        message.Subject = "Your account is ready";
        message.Body = new TextPart("plain")
        {
            Text = $"""
                An account was created for you.

                Email: {toEmail}
                Temporary password: {temporaryPassword}

                Please sign in and change this password as soon as possible.
                """,
        };

        await SendAsync(message, cancellationToken);
    }

    public async Task SendEmailConfirmationAsync(string toEmail, string confirmationLink, CancellationToken cancellationToken)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_options.FromName, _options.FromAddress));
        message.To.Add(MailboxAddress.Parse(toEmail));
        message.Subject = "Confirm your email";
        message.Body = new TextPart("plain")
        {
            Text = $"""
                Welcome! Please confirm your email address to finish setting up your account.

                {confirmationLink}

                If you didn't request this, you can ignore this email.
                """,
        };

        await SendAsync(message, cancellationToken);
    }

    public async Task SendPasswordResetAsync(string toEmail, string resetLink, CancellationToken cancellationToken)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_options.FromName, _options.FromAddress));
        message.To.Add(MailboxAddress.Parse(toEmail));
        message.Subject = "Reset your password";
        message.Body = new TextPart("plain")
        {
            Text = $"""
                A password reset was requested for this account.

                {resetLink}

                This link expires in 1 hour. If you didn't request this, you can ignore this
                email - your password will not change.
                """,
        };

        await SendAsync(message, cancellationToken);
    }

    public async Task SendStepUpCodeAsync(string toEmail, string code, CancellationToken cancellationToken)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_options.FromName, _options.FromAddress));
        message.To.Add(MailboxAddress.Parse(toEmail));
        message.Subject = "Your confirmation code";
        message.Body = new TextPart("plain")
        {
            Text = $"""
                Use this code to confirm it's really you before continuing:

                {code}

                This code expires in a few minutes. If you didn't request this, you can
                ignore this email.
                """,
        };

        await SendAsync(message, cancellationToken);
    }

    public async Task SendNewSignInNotificationAsync(
        string toEmail, string ipAddress, DateTime occurredAt, CancellationToken cancellationToken)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_options.FromName, _options.FromAddress));
        message.To.Add(MailboxAddress.Parse(toEmail));
        message.Subject = "New sign-in to your account";
        message.Body = new TextPart("plain")
        {
            Text = $"""
                Your account was just signed into from a network we haven't seen before.

                IP address: {ipAddress}
                Time (UTC): {occurredAt:u}

                If this was you, no action is needed. If it wasn't, reset your password and
                sign out of all other sessions as soon as possible.
                """,
        };

        await SendAsync(message, cancellationToken);
    }

    public async Task SendDuplicateRegistrationNoticeAsync(string toEmail, CancellationToken cancellationToken)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_options.FromName, _options.FromAddress));
        message.To.Add(MailboxAddress.Parse(toEmail));
        message.Subject = "Someone tried to register with your email";
        message.Body = new TextPart("plain")
        {
            Text = """
                Someone just tried to create a new account using this email address, which
                already has an account here.

                If this was you, sign in normally, or use "Forgot password" if you don't
                remember your password.

                If it wasn't you, no action is needed - your account is unaffected.
                """,
        };

        await SendAsync(message, cancellationToken);
    }

    public async Task SendEmailChangeConfirmationAsync(string toEmail, string confirmationLink, CancellationToken cancellationToken)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_options.FromName, _options.FromAddress));
        message.To.Add(MailboxAddress.Parse(toEmail));
        message.Subject = "Confirm your new email address";
        message.Body = new TextPart("plain")
        {
            Text = $"""
                Someone requested to change the email address on this account to this one.

                {confirmationLink}

                If this wasn't you, ignore this email - your account's email address will
                not change until this link is followed.
                """,
        };

        await SendAsync(message, cancellationToken);
    }

    private async Task SendAsync(MimeMessage message, CancellationToken cancellationToken)
    {
        using var client = new SmtpClient();

        var secureSocketOptions = _options.SmtpPort == 465
            ? SecureSocketOptions.SslOnConnect
            : _options.UseSsl
                ? SecureSocketOptions.StartTls
                : SecureSocketOptions.Auto;

        try
        {
            await client.ConnectAsync(_options.SmtpHost, _options.SmtpPort, secureSocketOptions, cancellationToken);

            // Both halves, not just the username: a username with no password was being
            // passed to AuthenticateAsync as null, which fails at the SMTP server rather
            // than where the configuration is actually wrong. Mailpit needs no auth at
            // all, so "neither is set" stays a normal, unauthenticated connection.
            if (!string.IsNullOrWhiteSpace(_options.SmtpUsername) &&
                !string.IsNullOrWhiteSpace(_options.SmtpPassword))
            {
                await client.AuthenticateAsync(_options.SmtpUsername, _options.SmtpPassword, cancellationToken);
            }

            await client.SendAsync(message, cancellationToken);
            logger.LogDebug("Sent email to {Recipients}", string.Join(", ", message.To));
        }
        finally
        {
            if (client.IsConnected)
            {
                await client.DisconnectAsync(true, cancellationToken);
            }
        }
    }
}
