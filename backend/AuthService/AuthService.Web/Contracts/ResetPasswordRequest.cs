namespace AuthService.Web.Contracts;

public record ResetPasswordRequest(string Email, string Token, string NewPassword);
