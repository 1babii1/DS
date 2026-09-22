namespace AuthService.Web.Contracts;

public record StepUpVerifyRequest(string Code);

/// <summary>True when the caller should submit an authenticator code directly to
/// /auth/step-up/verify; false means an email code was just sent and the caller should
/// request that instead. Lets the client render the right prompt without hardcoding a
/// guess about which method a given account uses.</summary>
public record StepUpStatusResponse(bool TwoFactorEnabled, DateTime? ElevatedUntil);
