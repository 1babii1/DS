using Fido2NetLib;

namespace AuthService.Web.Contracts;

public record PasskeyChallengeResponse(string Token, string OptionsJson);

public record CompletePasskeyRegistrationRequest(string Token, string Label, AuthenticatorAttestationRawResponse Attestation);

public record CompletePasskeyLoginRequest(string Token, AuthenticatorAssertionRawResponse Assertion);

public record PasskeySummary(Guid Id, string Name, DateTime CreatedAt, DateTime? LastUsedAt);
