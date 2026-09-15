namespace AuthService.Web.Configuration;

public interface IEmailSender
{
    Task SendNewAccountPasswordAsync(string toEmail, string temporaryPassword, CancellationToken cancellationToken);
}
