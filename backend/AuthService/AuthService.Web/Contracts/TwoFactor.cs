namespace AuthService.Web.Contracts;

public record TwoFactorSetupResponse(string SharedKey, string AuthenticatorUri);

public record Enable2faRequest(string Code);

public record Enable2faResponse(IReadOnlyList<string> RecoveryCodes);

public record Disable2faRequest(string Password);

public record RegenerateRecoveryCodesRequest(string Password);

public record RegenerateRecoveryCodesResponse(IReadOnlyList<string> RecoveryCodes);

public record TwoFactorLoginRequest(string Code, bool RememberClient);

public record TwoFactorRecoveryLoginRequest(string RecoveryCode);
