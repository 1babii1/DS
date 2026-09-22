namespace AuthService.Domain;

/// <summary>
/// A registered WebAuthn/passkey credential. CredentialId is the FIDO2 credential's own
/// globally-unique id (not this row's PK) - the usernameless login flow looks a credential
/// up by that value alone, before it knows which account it belongs to.
/// </summary>
public class PasskeyCredential
{
    public Guid Id { get; set; }

    public Guid AccountId { get; set; }

    public byte[] CredentialId { get; set; } = [];

    public byte[] PublicKey { get; set; } = [];

    public uint SignatureCounter { get; set; }

    public Guid AaGuid { get; set; }

    // A user-supplied label ("MacBook Touch ID", "YubiKey") - the only way to tell two
    // passkeys apart in the management UI, since the credential itself carries no name.
    public string Name { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime? LastUsedAt { get; set; }
}
