namespace AuthService.Domain;

public enum SigningKeyPurpose
{
    /// <summary>Signs access/identity tokens - plain JWTs every resource service validates
    /// locally via JWKS. Only needs a short grace window past retirement: those tokens live
    /// 15 minutes.</summary>
    Signing,

    /// <summary>Encrypts refresh tokens (JWE) - opaque to every service but AuthService
    /// itself, and live for up to 30 days, so a retired encryption key must keep validating
    /// for at least that long or every refresh token issued under it breaks outright.</summary>
    Encryption,
}

/// <summary>
/// One RSA key pair backing OpenIddict's signing or encryption credentials. Multiple rows
/// of the same purpose can be active at once - the newest is what signs/encrypts new
/// tokens, older ones stay registered purely so tokens already issued under them keep
/// validating until SigningKeyRotationJob retires them. See AuthenticationConfiguration for
/// how these get loaded into OpenIddict, and SigningKeyStore for the rotation logic itself.
/// </summary>
public class SigningKeyRecord
{
    public Guid Id { get; set; }

    public SigningKeyPurpose Purpose { get; set; }

    public string KeyId { get; set; } = string.Empty;

    /// <summary>PKCS#8 PEM private key. Stored as <c>enc:v1:...</c> (AES-256-GCM, bound to this
    /// row's purpose and key id) whenever <c>SigningKeys:AtRestKeyBase64</c> is configured, and as
    /// plaintext only in local development - see SigningKeyProtector.</summary>
    public string PrivateKeyPem { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    /// <summary>Null while still registered with OpenIddict for validation. Once set, the
    /// next application restart (or options reload) stops loading it - kept as a soft marker
    /// rather than an immediate hard delete so the row's own age is still inspectable.</summary>
    public DateTime? RetiredAt { get; set; }
}
