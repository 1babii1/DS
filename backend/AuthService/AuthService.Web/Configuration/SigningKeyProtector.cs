using System.Security.Cryptography;
using System.Text;
using AuthService.Domain;

namespace AuthService.Web.Configuration;

/// <summary>
/// Envelope protection for the RSA private keys kept in <c>auth.signing_keys</c>. Without it a
/// read-only leak of the database (a backup, a replica, an injection elsewhere) hands over the
/// keys that sign and encrypt every token this service issues. The master key comes from
/// configuration (<c>SigningKeys:AtRestKeyBase64</c>, 32 random bytes, base64) and therefore lives
/// outside the database.
/// Stored form: <c>enc:v1:</c> + base64(nonce[12] | tag[16] | ciphertext), AES-256-GCM. The
/// purpose and key id are authenticated as associated data, so a ciphertext copied onto another
/// row fails to decrypt instead of silently becoming that row's key.
/// Behaviour without a master key: values are stored and read as plaintext (local development and
/// tests only - Program.cs refuses to start in Production without one). Plaintext rows written
/// before the master key existed are still readable and are re-encrypted by
/// <see cref="SigningKeyStore.ProtectLegacyKeysAsync"/>. Rotating the master key itself is not
/// supported yet: it needs a decrypt-with-old / encrypt-with-new pass over every row.
/// </summary>
public sealed class SigningKeyProtector
{
    public const string Prefix = "enc:v1:";
    public const string ConfigurationKey = "SigningKeys:AtRestKeyBase64";

    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[]? _masterKey;

    public SigningKeyProtector(IConfiguration configuration)
    {
        var configured = configuration[ConfigurationKey];
        if (string.IsNullOrWhiteSpace(configured))
        {
            return;
        }

        try
        {
            _masterKey = Convert.FromBase64String(configured.Trim());
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException($"{ConfigurationKey} is not valid base64.", exception);
        }

        if (_masterKey.Length != 32)
        {
            throw new InvalidOperationException(
                $"{ConfigurationKey} must decode to exactly 32 bytes (got {_masterKey.Length}). Generate one with: openssl rand -base64 32");
        }
    }

    public bool IsEnabled => _masterKey is not null;

    public static bool IsProtected(string stored) => stored.StartsWith(Prefix, StringComparison.Ordinal);

    public string Protect(string pem, SigningKeyPurpose purpose, string keyId)
    {
        if (_masterKey is null)
        {
            return pem;
        }

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plaintext = Encoding.UTF8.GetBytes(pem);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(_masterKey, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(purpose, keyId));

        var packed = new byte[NonceSize + TagSize + ciphertext.Length];
        nonce.CopyTo(packed, 0);
        tag.CopyTo(packed, NonceSize);
        ciphertext.CopyTo(packed, NonceSize + TagSize);
        return Prefix + Convert.ToBase64String(packed);
    }

    public string Unprotect(string stored, SigningKeyPurpose purpose, string keyId)
    {
        if (!IsProtected(stored))
        {
            return stored;
        }

        if (_masterKey is null)
        {
            throw new InvalidOperationException(
                $"Signing key '{keyId}' is stored encrypted but {ConfigurationKey} is not configured.");
        }

        var packed = Convert.FromBase64String(stored[Prefix.Length..]);
        if (packed.Length < NonceSize + TagSize)
        {
            throw new CryptographicException($"Signing key '{keyId}' has a malformed encrypted value.");
        }

        var nonce = packed.AsSpan(0, NonceSize);
        var tag = packed.AsSpan(NonceSize, TagSize);
        var ciphertext = packed.AsSpan(NonceSize + TagSize);
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(_masterKey, TagSize);
        try
        {
            aes.Decrypt(nonce, ciphertext, tag, plaintext, AssociatedData(purpose, keyId));
        }
        catch (CryptographicException exception)
        {
            throw new CryptographicException(
                $"Signing key '{keyId}' could not be decrypted: wrong {ConfigurationKey}, or the stored value was altered or moved to another row.",
                exception);
        }

        return Encoding.UTF8.GetString(plaintext);
    }

    private static byte[] AssociatedData(SigningKeyPurpose purpose, string keyId) =>
        Encoding.UTF8.GetBytes($"{purpose}:{keyId}");
}
