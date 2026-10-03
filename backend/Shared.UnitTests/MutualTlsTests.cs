using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shared.Security;

namespace Shared.UnitTests;

// Mutual TLS (ADR 0044) with real handshakes: a server that requires a client certificate signed by the platform's authority, and clients
// with the right certificate, none, one from another authority, an expired one, and one that does not trust the server. Certificates are
// made here and written as PEM files the way a deployment supplies them.
[Collection("ProcessWideActivities")]
public sealed class MutualTlsTests : IAsyncLifetime
{
    private readonly string _directory = Directory.CreateTempSubdirectory("mtls-").FullName;
    private X509Certificate2 _authority = null!;
    private X509Certificate2 _rogueAuthority = null!;
    private WebApplication _app = null!;
    private string _url = null!;

    public async Task InitializeAsync()
    {
        _authority = NewAuthority("platform-ca");
        _rogueAuthority = NewAuthority("somebody-elses-ca");

        var server = Options("directory", NewLeaf("directory", _authority, server: true), _authority);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(IPAddress.Loopback, 0, listen =>
            {
                listen.Protocols = HttpProtocols.Http1AndHttp2;
                listen.UseMutualTls(server);
            }));
        _app = builder.Build();
        _app.MapGet("/who", (HttpContext context) => Results.Text(context.Connection.ClientCertificate?.Subject ?? "nobody"));
        await _app.StartAsync();
        _url = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        Directory.Delete(_directory, true);
    }

    private static X509Certificate2 NewAuthority(string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-400), DateTimeOffset.UtcNow.AddDays(400));
    }

    private static X509Certificate2 NewLeaf(string name, X509Certificate2 authority, bool server, bool expired = false)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid(server ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2")], false));
        if (server)
        {
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            names.AddDnsName("localhost");
            request.CertificateExtensions.Add(names.Build());
        }

        var from = expired ? DateTimeOffset.UtcNow.AddDays(-10) : DateTimeOffset.UtcNow.AddHours(-2);
        var until = expired ? DateTimeOffset.UtcNow.AddDays(-5) : DateTimeOffset.UtcNow.AddDays(7);
        using var signed = request.Create(authority, from, until, RandomNumberGenerator.GetBytes(8));
        return signed.CopyWithPrivateKey(key);
    }

    // Writes the service's certificate and key, and the authority it should trust, as the PEM files a deployment mounts.
    private MutualTlsOptions Options(string name, X509Certificate2 leaf, X509Certificate2 trustedAuthority)
    {
        var certificate = Path.Combine(_directory, $"{name}.pem");
        var key = Path.Combine(_directory, $"{name}.key");
        var authority = Path.Combine(_directory, $"{name}-ca.pem");
        File.WriteAllText(certificate, leaf.ExportCertificatePem());
        File.WriteAllText(key, leaf.GetECDsaPrivateKey()!.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(authority, trustedAuthority.ExportCertificatePem());
        return new MutualTlsOptions { Enabled = true, CertificatePath = certificate, KeyPath = key, CaPath = authority };
    }

    private HttpClient Client(MutualTlsOptions options) => new(MutualTls.CreateClientHandler(options)) { BaseAddress = new Uri(_url) };

    [Fact]
    public async Task A_client_with_a_certificate_from_the_platform_authority_gets_through_and_is_identified()
    {
        using var http = Client(Options("employee", NewLeaf("employee", _authority, server: false), _authority));

        var response = await http.GetAsync("/who");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("CN=employee", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_client_that_presents_no_certificate_is_refused_at_the_handshake()
    {
        // Trusts the server, presents nothing: a plain handler told to accept the platform's certificate.
        var serverAuthority = _authority;
        using var http = new HttpClient(new SocketsHttpHandler
        {
            SslOptions = { RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null && MutualTls.IsSignedBy(new X509Certificate2(certificate), serverAuthority) },
        }) { BaseAddress = new Uri(_url) };

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.GetAsync("/who"));
    }

    [Fact]
    public async Task A_client_with_a_certificate_from_another_authority_is_refused()
    {
        using var http = Client(Options("intruder", NewLeaf("intruder", _rogueAuthority, server: false), _authority));

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.GetAsync("/who"));
    }

    [Fact]
    public async Task A_client_with_an_expired_certificate_from_the_right_authority_is_refused()
    {
        using var http = Client(Options("stale", NewLeaf("stale", _authority, server: false, expired: true), _authority));

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.GetAsync("/who"));
    }

    [Fact]
    public async Task A_client_does_not_talk_to_a_server_whose_certificate_the_platform_authority_did_not_sign()
    {
        // The client holds a good certificate but trusts only the other authority, so it must refuse this server.
        using var http = Client(Options("suspicious", NewLeaf("suspicious", _authority, server: false), _rogueAuthority));

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.GetAsync("/who"));
    }

    [Fact]
    public void A_certificate_is_judged_by_the_platform_authority_alone_not_by_the_machines_trust_store()
    {
        var leaf = NewLeaf("x", _authority, server: false);

        Assert.True(MutualTls.IsSignedBy(leaf, _authority));
        Assert.False(MutualTls.IsSignedBy(leaf, _rogueAuthority));
    }

    // The certificates a deployment is actually given come from openssl (scripts/mtls-certs.sh), not from .NET. This runs the script
    // and does a real handshake with what it wrote, so that a format the runtime cannot read (a key in the wrong encoding, an
    // extension it rejects) shows up here and not on the first deploy.
    [Fact]
    public async Task The_certificates_the_openssl_script_writes_work_for_a_real_handshake()
    {
        var script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "scripts", "mtls-certs.sh"));
        var certs = Path.Combine(_directory, "openssl");
        using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("bash", [script, certs])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!)
        {
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, error);
        }

        MutualTlsOptions Of(string service) => new()
        {
            Enabled = true,
            CertificatePath = Path.Combine(certs, $"{service}.pem"),
            KeyPath = Path.Combine(certs, $"{service}.key"),
            CaPath = Path.Combine(certs, "ca.pem"),
        };

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen =>
        {
            listen.Protocols = HttpProtocols.Http1AndHttp2;
            listen.UseMutualTls(Of("directory_service"));
        }));
        await using var app = builder.Build();
        app.MapGet("/who", (HttpContext context) => Results.Text(context.Connection.ClientCertificate?.Subject ?? "nobody"));
        await app.StartAsync();
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

        using var http = new HttpClient(MutualTls.CreateClientHandler(Of("employee_service"))) { BaseAddress = new Uri(url) };
        var response = await http.GetAsync("/who");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("CN=employee_service", await response.Content.ReadAsStringAsync());
    }
}
