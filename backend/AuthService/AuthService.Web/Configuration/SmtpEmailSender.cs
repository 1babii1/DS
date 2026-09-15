using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace AuthService.Web.Configuration;

// Used by EmployeeEventsConsumer to hand a newly hired employee their generated
// password (the Hire Employee saga's only real credential-delivery mechanism -
// there's no self-service "set your password" flow in this project). A failed send
// is logged but never fails the provisioning step itself: the account already
// exists by the time this runs, so a delivery failure is an ops problem to retry
// manually, not a reason to compensate the whole saga.
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

            if (!string.IsNullOrWhiteSpace(_options.SmtpUsername))
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
