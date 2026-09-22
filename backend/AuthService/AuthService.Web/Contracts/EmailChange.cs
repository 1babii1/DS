namespace AuthService.Web.Contracts;

public record RequestEmailChangeRequest(string NewEmail, string Password);

public record ConfirmEmailChangeRequest(Guid UserId, string NewEmail, string Token);
