using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;

namespace AuthService.IntegrationTests.Infrastructure;

/// <summary>
/// A hand-built "none"-attestation FIDO2 authenticator, standing in for a real security key
/// or platform authenticator so the register/login ceremonies can be driven end-to-end over
/// HTTP - no browser, no real hardware. Builds the exact wire format a browser's
/// navigator.credentials.create/get would produce (CBOR attestationObject, COSE EC2 public
/// key, DER-encoded ECDSA assertion signature) so this proves PasskeyService's actual
/// verification path, not just its own database plumbing.
/// </summary>
public sealed class Fido2TestAuthenticator
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly byte[] _credentialId = RandomNumberGenerator.GetBytes(32);
    private readonly Guid _aaGuid = Guid.NewGuid();
    private uint _signCount = 1;

    public AuthenticatorAttestationRawResponse MakeAttestation(string challengeBase64Url, string origin, string rpId)
    {
        var clientDataJson = BuildClientDataJson("webauthn.create", challengeBase64Url, origin);
        var authenticatorData = BuildAuthenticatorData(rpId, includeAttestedCredential: true);
        var attestationObject = BuildNoneAttestationObject(authenticatorData);

        return new AuthenticatorAttestationRawResponse
        {
            Id = Base64UrlEncode(_credentialId),
            RawId = _credentialId,
            Type = PublicKeyCredentialType.PublicKey,
            Response = new AuthenticatorAttestationRawResponse.AttestationResponse
            {
                AttestationObject = attestationObject,
                ClientDataJson = clientDataJson,
                Transports = [],
            },
            ClientExtensionResults = new AuthenticationExtensionsClientOutputs(),
        };
    }

    public AuthenticatorAssertionRawResponse MakeAssertion(
        string challengeBase64Url, string origin, string rpId, byte[]? userHandle)
    {
        var clientDataJson = BuildClientDataJson("webauthn.get", challengeBase64Url, origin);
        var authenticatorData = BuildAuthenticatorData(rpId, includeAttestedCredential: false);

        var signedData = new byte[authenticatorData.Length + 32];
        authenticatorData.CopyTo(signedData, 0);
        SHA256.HashData(clientDataJson).CopyTo(signedData, authenticatorData.Length);
        var signature = _key.SignData(signedData, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        return new AuthenticatorAssertionRawResponse
        {
            Id = Base64UrlEncode(_credentialId),
            RawId = _credentialId,
            Type = PublicKeyCredentialType.PublicKey,
            Response = new AuthenticatorAssertionRawResponse.AssertionResponse
            {
                AuthenticatorData = authenticatorData,
                ClientDataJson = clientDataJson,
                Signature = signature,
                UserHandle = userHandle,
            },
            ClientExtensionResults = new AuthenticationExtensionsClientOutputs(),
        };
    }

    private static byte[] BuildClientDataJson(string type, string challengeBase64Url, string origin) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            type,
            challenge = challengeBase64Url,
            origin,
            crossOrigin = false,
        }));

    private byte[] BuildAuthenticatorData(string rpId, bool includeAttestedCredential)
    {
        var rpIdHash = SHA256.HashData(Encoding.UTF8.GetBytes(rpId));

        // Flags: bit 0 (UP, user present) + bit 2 (UV, user verified) always set for this
        // fake authenticator; bit 6 (AT, attested credential data present) only on registration.
        byte flags = 0b0000_0101;
        if (includeAttestedCredential)
        {
            flags |= 0b0100_0000;
        }

        using var stream = new MemoryStream();
        stream.Write(rpIdHash);
        stream.WriteByte(flags);

        var counterBytes = BitConverter.GetBytes(_signCount);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(counterBytes);
        }

        stream.Write(counterBytes);
        _signCount++;

        if (includeAttestedCredential)
        {
            stream.Write(_aaGuid.ToByteArray());

            var credentialIdLength = BitConverter.GetBytes((ushort)_credentialId.Length);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(credentialIdLength);
            }

            stream.Write(credentialIdLength);
            stream.Write(_credentialId);
            stream.Write(BuildCoseP256PublicKey());
        }

        return stream.ToArray();
    }

    /// <summary>COSE_Key CBOR map for an EC2 P-256 (ES256) public key - the exact shape
    /// Fido2NetLib decodes back out of attestedCredentialData on registration.</summary>
    private byte[] BuildCoseP256PublicKey()
    {
        var parameters = _key.ExportParameters(includePrivateParameters: false);

        var writer = new CborWriter(CborConformanceMode.Ctap2Canonical);
        writer.WriteStartMap(5);
        writer.WriteInt32(1); // kty
        writer.WriteInt32(2); // EC2
        writer.WriteInt32(3); // alg
        writer.WriteInt32(-7); // ES256
        writer.WriteInt32(-1); // crv
        writer.WriteInt32(1); // P-256
        writer.WriteInt32(-2); // x
        writer.WriteByteString(parameters.Q.X!);
        writer.WriteInt32(-3); // y
        writer.WriteByteString(parameters.Q.Y!);
        writer.WriteEndMap();
        return writer.Encode();
    }

    private static byte[] BuildNoneAttestationObject(byte[] authenticatorData)
    {
        var writer = new CborWriter(CborConformanceMode.Ctap2Canonical);
        writer.WriteStartMap(3);
        writer.WriteTextString("fmt");
        writer.WriteTextString("none");
        writer.WriteTextString("attStmt");
        writer.WriteStartMap(0);
        writer.WriteEndMap();
        writer.WriteTextString("authData");
        writer.WriteByteString(authenticatorData);
        writer.WriteEndMap();
        return writer.Encode();
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
