using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Configuration;

namespace Shared.Security;

/// <summary>
/// Mutual TLS between two services (ADR 0044): the caller proves who it is with a certificate and so does the callee, both signed by a
/// certificate authority of the platform. Configured under <c>MutualTls:</c>; off unless <c>Enabled</c> is true, so nothing changes
/// until a deployment opts in. Files are PEM: the service's certificate, its private key, and the authority that signs the platform's certificates.
/// </summary>
public sealed class MutualTlsOptions
{
    public const string SectionName = "MutualTls";

    public bool Enabled { get; set; }

    public string CertificatePath { get; set; } = string.Empty;

    public string KeyPath { get; set; } = string.Empty;

    public string CaPath { get; set; } = string.Empty;

    public static MutualTlsOptions From(IConfiguration configuration) =>
        configuration.GetSection(SectionName).Get<MutualTlsOptions>() ?? new MutualTlsOptions();

    public X509Certificate2 LoadCertificate() => X509Certificate2.CreateFromPemFile(CertificatePath, KeyPath);

    public X509Certificate2 LoadAuthority() => X509CertificateLoader.LoadCertificateFromFile(CaPath);
}

public static class MutualTls
{
    /// <summary>
    /// Whether <paramref name="certificate"/> chains to <paramref name="authority"/> and to nothing else. The system's trust store is
    /// deliberately not consulted: a certificate from a public authority is not a member of this platform.
    /// </summary>
    public static bool IsSignedBy(X509Certificate2 certificate, X509Certificate2 authority, X509Certificate2Collection? intermediates = null)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(authority);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        if (intermediates is not null)
        {
            chain.ChainPolicy.ExtraStore.AddRange(intermediates);
        }

        return chain.Build(certificate);
    }

    /// <summary>The server side: HTTPS with this service's certificate, and a client certificate required and checked against the authority.</summary>
    public static void UseMutualTls(this ListenOptions listen, MutualTlsOptions options)
    {
        var authority = options.LoadAuthority();
        listen.UseHttps(https =>
        {
            https.ServerCertificate = options.LoadCertificate();
            https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
            https.ClientCertificateValidation = (certificate, _, _) => IsSignedBy(certificate, authority);
        });
    }

    /// <summary>The client side: present this service's certificate, and accept a server only if its certificate is signed by the authority.</summary>
    public static SocketsHttpHandler CreateClientHandler(MutualTlsOptions options)
    {
        var authority = options.LoadAuthority();
        var handler = new SocketsHttpHandler();
        handler.SslOptions.ClientCertificates = [options.LoadCertificate()];
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
            certificate is not null
            && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None
            && IsSignedBy(new X509Certificate2(certificate), authority);
        return handler;
    }
}
