using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Http;
using SeoTester.Crawler;
using SeoTester.Tls;

namespace SeoTester.Tests.Crawler;

/// <summary>
/// Spike S2: the legacy-TLS probe against loopback servers that answer with canned records (no third-party host), and
/// the platform handshake against a loopback TLS server with a self-signed certificate, through the crawler's
/// SSRF-safe sockets (loopback opened for the test only).
/// </summary>
public class TlsProbeTests
{
    /// <summary>A server that reads one ClientHello and answers <paramref name="reply"/> (or nothing, holding the connection).</summary>
    private static async Task<(int Port, Task<byte[]> Hello)> CannedServerAsync(Func<byte[], byte[]?> reply)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var hello = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var header = new byte[5];
            await stream.ReadExactlyAsync(header);
            var body = new byte[BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(3))];
            await stream.ReadExactlyAsync(body);
            var answer = reply(body);
            if (answer is null)
                await Task.Delay(2000);
            else
                await stream.WriteAsync(answer);
            listener.Stop();
            return body;
        });
        await Task.Yield();
        return (port, hello);
    }

    private static byte[] ServerHello(ushort version)
    {
        var hello = new byte[] { 2, 0, 0, 38, (byte)(version >> 8), (byte)version }.Concat(new byte[32]).Concat(new byte[] { 0, 0x00, 0x2F, 0 }).ToArray();
        return new byte[] { 22, 3, 1, 0, (byte)hello.Length }.Concat(hello).ToArray();
    }

    private static async Task<LegacyTlsAnswer> ProbeAsync(int port, ushort version, TimeSpan? timeout = null)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        return await LegacyTlsProbe.ProbeAsync(client.GetStream(), "example.com", version, timeout ?? TimeSpan.FromSeconds(5), CancellationToken.None);
    }

    [Fact]
    public async Task A_server_hello_with_the_offered_legacy_version_means_accepted()
    {
        var (port, hello) = await CannedServerAsync(_ => ServerHello(LegacyTlsProbe.Tls10));

        (await ProbeAsync(port, LegacyTlsProbe.Tls10)).Should().Be(LegacyTlsAnswer.Accepted);

        var body = await hello;
        body[0].Should().Be(1, "a ClientHello");
        BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(4)).Should().Be(LegacyTlsProbe.Tls10, "the client version offered is TLS 1.0 only");
        System.Text.Encoding.ASCII.GetString(body).Should().Contain("example.com", "SNI names the host");
    }

    [Fact]
    public async Task An_alert_another_version_or_a_closed_connection_means_refused()
    {
        var (alertPort, _) = await CannedServerAsync(_ => [21, 3, 1, 0, 2, 2, 70]); // fatal protocol_version
        (await ProbeAsync(alertPort, LegacyTlsProbe.Tls11)).Should().Be(LegacyTlsAnswer.Refused);

        var (upgradePort, _) = await CannedServerAsync(_ => ServerHello(0x0303));
        (await ProbeAsync(upgradePort, LegacyTlsProbe.Tls11)).Should().Be(LegacyTlsAnswer.Refused);

        var (closePort, _) = await CannedServerAsync(_ => []);
        (await ProbeAsync(closePort, LegacyTlsProbe.Tls10)).Should().Be(LegacyTlsAnswer.Refused);
    }

    [Fact]
    public async Task Silence_or_something_that_is_not_tls_proves_nothing()
    {
        var (silentPort, _) = await CannedServerAsync(_ => null);
        (await ProbeAsync(silentPort, LegacyTlsProbe.Tls10, TimeSpan.FromMilliseconds(300))).Should().Be(LegacyTlsAnswer.Inconclusive);

        var (httpPort, _) = await CannedServerAsync(_ => "HTTP/1.1 400 Bad Request\r\n\r\n"u8.ToArray());
        (await ProbeAsync(httpPort, LegacyTlsProbe.Tls10)).Should().Be(LegacyTlsAnswer.Inconclusive);
    }

    [Fact]
    public async Task The_platform_handshake_reads_the_certificate_version_and_alpn_through_the_safe_sockets()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=example.com", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("example.com");
        request.CertificateExtensions.Add(san.Build());
        var notAfter = DateTimeOffset.UtcNow.AddDays(20);
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), notAfter);
        using var certificate = X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            // One ordinary handshake, then the two legacy hellos (which this server, like any modern one, refuses).
            for (var i = 0; i < 3; i++)
            {
                using var client = await listener.AcceptTcpClientAsync();
                try
                {
                    await using var ssl = new SslStream(client.GetStream());
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = certificate,
                        // A modern server. (Windows' own default still answered a TLS 1.0 hello on the build machine,
                        // which the legacy probe reported correctly; pinning makes the test independent of the OS.)
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        ApplicationProtocols = [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11],
                    });
                    await Task.Delay(100);
                }
                catch (Exception ex) when (ex is AuthenticationException or IOException)
                {
                }
            }
            listener.Stop();
        });

        var sockets = BuildSockets(port);
        var prober = new TlsProber(sockets, NullLogger<TlsProber>.Instance);

        var facts = await prober.ProbeAsync("127.0.0.1", port, CancellationToken.None);
        await server;

        facts.Handshake.Should().BeTrue(facts.Error);
        new[] { "Tls12", "Tls13" }.Should().Contain(facts.Protocol!);
        facts.Alpn.Should().Be("h2");
        facts.CertificateNotAfter!.Value.Should().BeCloseTo(notAfter.UtcDateTime, TimeSpan.FromSeconds(2));
        facts.CertificateProblem.Should().NotBeNull("a self-signed certificate for another name is not trusted");
        facts.Tls10.Should().Be(LegacyTlsAnswer.Refused);
        facts.Tls11.Should().Be(LegacyTlsAnswer.Refused);
    }

    [Fact]
    public async Task The_tls_probe_cannot_reach_a_private_address()
    {
        var prober = new TlsProber(BuildSockets(port: null), NullLogger<TlsProber>.Instance);

        var facts = await prober.ProbeAsync("127.0.0.1", 443, CancellationToken.None);

        facts.Handshake.Should().BeFalse();
        facts.Error.Should().Be("refused: Address");
        facts.Tls10.HasValue.Should().BeFalse("no legacy hello goes out when the address is refused");
    }

    private static ISafeHttpSockets BuildSockets(int? port)
    {
        var services = new ServiceCollection();
        services.AddSafeHttpClient(SeoHttp.Crawler, configure: o =>
        {
            if (port is { } p)
            {
                o.AllowedNetworks.Add("127.0.0.1/32");
                o.AllowedPorts.Add(p);
            }
        });
        return services.BuildServiceProvider().GetRequiredService<ISafeHttpSockets>();
    }
}
