using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Configuration;
using NSubstitute;
using Raven.Client.Documents;
using Raven.Client.ServerWide.Operations;

namespace MintPlayer.Spark.Tests.Builder;

/// <summary>
/// <c>Spark:RavenDb:CertificateFile</c> and friends: a Spark app reaches a secured RavenDB server
/// (HTTPS, client certificates, a server certificate from a private authority) from configuration
/// alone (SeoTester PRD §12, the shared-RavenDB hardening). Every certificate is made in the test;
/// nothing is read from the machine. Without the settings the store is the unsecured one of local
/// development, unchanged.
/// </summary>
public sealed class SparkRavenDbSecurityTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("spark-raven-tls-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
    }

    // ── Certificates ─────────────────────────────────────────────────────────────────────────

    private static X509Certificate2 Authority(string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    private static X509Certificate2 Issue(X509Certificate2 authority, string name, bool server)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [server ? new Oid("1.3.6.1.5.5.7.3.1") : new Oid("1.3.6.1.5.5.7.3.2")], false));
        if (server)
        {
            var san = new SubjectAlternativeNameBuilder();
            san.AddIpAddress(IPAddress.Loopback);
            san.AddDnsName(name);
            request.CertificateExtensions.Add(san.Build());
        }
        var serial = RandomNumberGenerator.GetBytes(12);
        using var issued = request.Create(authority, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(7), serial);
        var withKey = issued.CopyWithPrivateKey(key);
        // Through PKCS#12 so Windows' TLS stack accepts the key (as the code under test does).
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
    }

    private string WritePem(X509Certificate2 certificate, string name, bool withKey)
    {
        var certFile = Path.Combine(directory, name + ".crt");
        File.WriteAllText(certFile, certificate.ExportCertificatePem());
        if (withKey)
            File.WriteAllText(Path.Combine(directory, name + ".key"), certificate.GetECDsaPrivateKey()!.ExportPkcs8PrivateKeyPem());
        return certFile;
    }

    private string WritePfx(X509Certificate2 certificate, string name, string password)
    {
        var file = Path.Combine(directory, name + ".pfx");
        File.WriteAllBytes(file, certificate.Export(X509ContentType.Pkcs12, password));
        return file;
    }

    // ── AddSpark's store ─────────────────────────────────────────────────────────────────────

    private static IDocumentStore StoreFrom(Dictionary<string, string?> settings)
    {
        settings.TryAdd("Spark:RavenDb:Database", "Probe");
        // No connection attempt and no database check: these tests are about the store's settings.
        settings.TryAdd("Spark:RavenDb:MaxConnectionRetries", "0");
        settings.TryAdd("Spark:RavenDb:EnsureDatabaseCreated", "false");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(Environments.Production);
        services.AddSingleton(env);
        services.AddSpark(configuration, _ => { });
        return services.BuildServiceProvider().GetRequiredService<IDocumentStore>();
    }

    [Fact]
    public void Without_certificate_settings_the_store_has_no_certificate()
    {
        var store = StoreFrom(new() { ["Spark:RavenDb:Urls:0"] = "http://localhost:8080" });

        store.Certificate.Should().BeNull();
        store.Urls.Should().BeEquivalentTo(new[] { "http://localhost:8080" });
    }

    [Fact]
    public void A_PEM_certificate_and_key_become_the_stores_client_certificate()
    {
        using var ca = Authority("Spark test CA");
        using var client = Issue(ca, "spark-app", server: false);
        var certFile = WritePem(client, "client", withKey: true);

        var store = StoreFrom(new()
        {
            ["Spark:RavenDb:Urls:0"] = "https://raven.internal:8080",
            ["Spark:RavenDb:CertificateFile"] = certFile,
            ["Spark:RavenDb:CertificateKeyFile"] = Path.Combine(directory, "client.key"),
        });

        store.Certificate.Should().NotBeNull();
        store.Certificate!.Thumbprint.Should().Be(client.Thumbprint);
        store.Certificate.HasPrivateKey.Should().BeTrue();
    }

    [Fact]
    public void A_password_protected_pfx_becomes_the_stores_client_certificate()
    {
        using var ca = Authority("Spark test CA");
        using var client = Issue(ca, "spark-app", server: false);
        var pfx = WritePfx(client, "client", "s3cret-for-the-test");

        var store = StoreFrom(new()
        {
            ["Spark:RavenDb:Urls:0"] = "https://raven.internal:8080",
            ["Spark:RavenDb:CertificateFile"] = pfx,
            ["Spark:RavenDb:CertificatePassword"] = "s3cret-for-the-test",
        });

        store.Certificate!.Thumbprint.Should().Be(client.Thumbprint);
    }

    [Fact]
    public void A_certificate_for_an_http_url_refuses_startup()
    {
        using var ca = Authority("Spark test CA");
        using var client = Issue(ca, "spark-app", server: false);
        var certFile = WritePem(client, "client", withKey: true);

        var act = () => StoreFrom(new()
        {
            ["Spark:RavenDb:Urls:0"] = "http://raven.internal:8080",
            ["Spark:RavenDb:CertificateFile"] = certFile,
            ["Spark:RavenDb:CertificateKeyFile"] = Path.Combine(directory, "client.key"),
        });

        act.Should().Throw<InvalidOperationException>().WithMessage("*not https://*");
    }

    [Fact]
    public void A_key_without_a_certificate_refuses_startup()
    {
        var act = () => StoreFrom(new()
        {
            ["Spark:RavenDb:Urls:0"] = "https://raven.internal:8080",
            ["Spark:RavenDb:CertificateKeyFile"] = Path.Combine(directory, "client.key"),
        });

        act.Should().Throw<InvalidOperationException>().WithMessage("*CertificateKeyFile*");
    }

    [Fact]
    public void A_certificate_without_its_private_key_refuses_startup()
    {
        using var ca = Authority("Spark test CA");
        using var client = Issue(ca, "spark-app", server: false);
        var certFile = WritePem(client, "public-only", withKey: false);

        var act = () => StoreFrom(new()
        {
            ["Spark:RavenDb:Urls:0"] = "https://raven.internal:8080",
            ["Spark:RavenDb:CertificateFile"] = certFile,
        });

        // The PEM holds no key: the loader refuses it rather than sending an unusable certificate.
        act.Should().Throw<Exception>();
    }

    // ── The server's authority ───────────────────────────────────────────────────────────────

    [Fact]
    public void The_authority_accepts_only_its_own_certificates_and_never_a_wrong_name()
    {
        using var ca = Authority("Spark test CA");
        using var other = Authority("Someone else's CA");
        using var ours = Issue(ca, "coverage-raven", server: true);
        using var theirs = Issue(other, "coverage-raven", server: true);
        var authority = new SparkRavenDbSecurity.ServerAuthority(X509CertificateLoader.LoadCertificate(ca.RawData));

        authority.Validate(this, ours, null, SslPolicyErrors.RemoteCertificateChainErrors).Should().BeTrue();
        authority.Validate(this, theirs, null, SslPolicyErrors.RemoteCertificateChainErrors).Should().BeFalse();
        authority.Validate(this, ours, null, SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch).Should().BeFalse();
        authority.Validate(this, null, null, SslPolicyErrors.RemoteCertificateNotAvailable).Should().BeFalse();
        // A publicly trusted certificate (no errors) keeps working for every other store in the process.
        authority.Validate(this, theirs, null, SslPolicyErrors.None).Should().BeTrue();
    }

    /// <summary>
    /// End to end over a real TLS socket: a Kestrel server with a certificate from a private
    /// authority that REQUIRES a client certificate, and AddSpark's store configured with the
    /// client certificate and the authority. The server records who connected; it is not RavenDB,
    /// so the operation itself fails after the handshake, which is not what is tested.
    /// </summary>
    [Fact]
    public async Task The_store_completes_the_handshake_with_a_private_server_authority_and_presents_its_certificate()
    {
        using var ca = Authority("Spark test CA " + Guid.NewGuid().ToString("N"));
        using var serverCertificate = Issue(ca, "localhost", server: true);
        using var client = Issue(ca, "spark-app", server: false);
        var seen = new List<string>();

        await using var server = await StartTlsServer(serverCertificate, seen);
        var store = StoreFrom(new()
        {
            ["Spark:RavenDb:Urls:0"] = server.Url,
            ["Spark:RavenDb:CertificateFile"] = WritePem(client, "client", withKey: true),
            ["Spark:RavenDb:CertificateKeyFile"] = Path.Combine(directory, "client.key"),
            ["Spark:RavenDb:ServerCertificateAuthorityFile"] = WritePem(ca, "ca", withKey: false),
        });

        try { store.Maintenance.Server.Send(new GetDatabaseNamesOperation(0, 1)); } catch (Exception) { }

        lock (seen) seen.Should().NotBeEmpty();
        lock (seen) seen.All(t => t == client.Thumbprint).Should().BeTrue();
    }

    [Fact]
    public async Task A_server_certificate_from_another_authority_is_refused()
    {
        using var ca = Authority("Spark test CA " + Guid.NewGuid().ToString("N"));
        using var stranger = Authority("Stranger CA " + Guid.NewGuid().ToString("N"));
        using var serverCertificate = Issue(stranger, "localhost", server: true);
        using var client = Issue(ca, "spark-app", server: false);
        var seen = new List<string>();

        await using var server = await StartTlsServer(serverCertificate, seen);
        var store = StoreFrom(new()
        {
            ["Spark:RavenDb:Urls:0"] = server.Url,
            ["Spark:RavenDb:CertificateFile"] = WritePem(client, "client", withKey: true),
            ["Spark:RavenDb:CertificateKeyFile"] = Path.Combine(directory, "client.key"),
            ["Spark:RavenDb:ServerCertificateAuthorityFile"] = WritePem(ca, "ca", withKey: false),
        });

        var act = () => store.Maintenance.Server.Send(new GetDatabaseNamesOperation(0, 1));

        act.Should().Throw<Exception>();
        lock (seen) seen.Should().BeEmpty();
    }

    private sealed class TlsServer(WebApplication app, string url) : IAsyncDisposable
    {
        public string Url { get; } = url;
        public async ValueTask DisposeAsync() => await app.DisposeAsync();
    }

    private static async Task<TlsServer> StartTlsServer(X509Certificate2 certificate, List<string> seen)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(https =>
        {
            https.ServerCertificate = certificate;
            https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
            https.AllowAnyClientCertificate();
        })));
        var app = builder.Build();
        app.Run(context =>
        {
            var thumbprint = context.Connection.ClientCertificate?.Thumbprint ?? "(none)";
            lock (seen) seen.Add(thumbprint);
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Task.CompletedTask;
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new TlsServer(app, address.Replace("[::1]", "127.0.0.1"));
    }
}
