using System.Security.Cryptography;
using AuthService.Domain;
using AuthService.Web.Configuration;
using Microsoft.Extensions.Configuration;

namespace AuthService.IntegrationTests;

// Pure unit tests (no host, no database): what SigningKeyProtector promises about the private
// keys it wraps - confidentiality, tamper detection, binding to the row, and how it behaves when
// the master key is absent, wrong, or malformed.
public class SigningKeyProtectorTests
{
    private const string Pem = "-----BEGIN PRIVATE KEY-----\nMIIEvQIBADANBgkqhkiG9w0BAQEFAASC\n-----END PRIVATE KEY-----\n";

    [Fact]
    public void A_protected_key_round_trips_and_does_not_contain_the_plaintext()
    {
        var protector = Create(RandomKey());

        var stored = protector.Protect(Pem, SigningKeyPurpose.Signing, "kid-1");

        Assert.StartsWith(SigningKeyProtector.Prefix, stored, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", stored, StringComparison.Ordinal);
        Assert.Equal(Pem, protector.Unprotect(stored, SigningKeyPurpose.Signing, "kid-1"));
    }

    [Fact]
    public void Protecting_the_same_key_twice_gives_different_ciphertexts()
    {
        var protector = Create(RandomKey());

        var first = protector.Protect(Pem, SigningKeyPurpose.Signing, "kid-1");
        var second = protector.Protect(Pem, SigningKeyPurpose.Signing, "kid-1");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void A_ciphertext_moved_to_another_row_does_not_decrypt()
    {
        var protector = Create(RandomKey());
        var stored = protector.Protect(Pem, SigningKeyPurpose.Signing, "kid-1");

        Assert.Throws<CryptographicException>(() => protector.Unprotect(stored, SigningKeyPurpose.Signing, "kid-2"));
        Assert.Throws<CryptographicException>(() => protector.Unprotect(stored, SigningKeyPurpose.Encryption, "kid-1"));
    }

    [Fact]
    public void A_tampered_ciphertext_is_rejected()
    {
        var protector = Create(RandomKey());
        var stored = protector.Protect(Pem, SigningKeyPurpose.Signing, "kid-1");
        var bytes = Convert.FromBase64String(stored[SigningKeyProtector.Prefix.Length..]);
        bytes[^1] ^= 0x01;
        var tampered = SigningKeyProtector.Prefix + Convert.ToBase64String(bytes);

        Assert.Throws<CryptographicException>(() => protector.Unprotect(tampered, SigningKeyPurpose.Signing, "kid-1"));
    }

    [Fact]
    public void The_wrong_master_key_cannot_decrypt()
    {
        var stored = Create(RandomKey()).Protect(Pem, SigningKeyPurpose.Signing, "kid-1");

        var exception = Assert.Throws<CryptographicException>(
            () => Create(RandomKey()).Unprotect(stored, SigningKeyPurpose.Signing, "kid-1"));
        Assert.Contains("wrong", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_encrypted_value_without_a_configured_master_key_fails_loudly_instead_of_returning_garbage()
    {
        var stored = Create(RandomKey()).Protect(Pem, SigningKeyPurpose.Signing, "kid-1");

        var exception = Assert.Throws<InvalidOperationException>(
            () => Create(null).Unprotect(stored, SigningKeyPurpose.Signing, "kid-1"));
        Assert.Contains(SigningKeyProtector.ConfigurationKey, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_master_key_values_pass_through_unchanged_for_local_development()
    {
        var protector = Create(null);

        Assert.False(protector.IsEnabled);
        Assert.Equal(Pem, protector.Protect(Pem, SigningKeyPurpose.Signing, "kid-1"));
        Assert.Equal(Pem, protector.Unprotect(Pem, SigningKeyPurpose.Signing, "kid-1"));
    }

    [Fact]
    public void A_legacy_plaintext_row_is_still_readable_once_a_master_key_is_configured()
    {
        Assert.Equal(Pem, Create(RandomKey()).Unprotect(Pem, SigningKeyPurpose.Signing, "kid-1"));
    }

    [Theory]
    [InlineData("not base64 at all !!!")]
    [InlineData("AAAA")]
    public void A_malformed_master_key_is_rejected_at_startup(string configured)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Create(configured));

        Assert.Contains(SigningKeyProtector.ConfigurationKey, exception.Message, StringComparison.Ordinal);
    }

    private static string RandomKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static SigningKeyProtector Create(string? key) =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [SigningKeyProtector.ConfigurationKey] = key })
            .Build());
}
