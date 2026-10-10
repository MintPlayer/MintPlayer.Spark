using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using MintPlayer.Spark.Http;
using SeoTester.Checks;
using SeoTester.Tls;

namespace SeoTester.Crawler;

/// <summary>The TLS facts of a host (security.https, certificate-expiry, tls-legacy, tls13, performance.http2).</summary>
public interface ITlsProber
{
    Task<TlsFacts> ProbeAsync(string host, int port, CancellationToken cancellationToken);
}

/// <summary>
/// Spike S2 (<c>docs/seo_analyzer_PRD.md</c> §8): one ordinary handshake with the platform's TLS (OpenSSL 3 in the
/// Debian image) for the certificate, the negotiated version and ALPN, and two hand-built ClientHellos
/// (<see cref="LegacyTlsProbe"/>) for TLS 1.0 and 1.1, which the platform refuses to speak. Every connection goes
/// through <see cref="ISafeHttpSockets"/> under the crawler client's rules: the same address allow-list, no DNS
/// rebinding, the same ports. HTTP/3 is read from <c>Alt-Svc</c> only (no <c>libmsquic</c> in the image, S2 decision).
/// </summary>
public sealed class TlsProber(ISafeHttpSockets sockets, ILogger<TlsProber> logger) : ITlsProber
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    public async Task<TlsFacts> ProbeAsync(string host, int port, CancellationToken cancellationToken)
    {
        bool handshake = false;
        string? error = null, protocol = null, alpn = null, problem = null, issuer = null;
        DateTime? notAfter = null;
        try
        {
            await using var stream = await sockets.ConnectAsync(SeoHttp.Tls, host, port, cancellationToken);
            SslPolicyErrors errors = SslPolicyErrors.None;
            X509ChainStatusFlags chainStatus = X509ChainStatusFlags.NoError;
            X509Certificate2? certificate = null;
            await using var ssl = new SslStream(stream, leaveInnerStreamOpen: false, (_, cert, chain, policyErrors) =>
            {
                errors = policyErrors;
                if (cert is not null)
                    certificate = new X509Certificate2(cert);
                foreach (var status in chain?.ChainStatus ?? [])
                    chainStatus |= status.Status;
                return true; // inspect, never trust: nothing is sent over this connection
            });
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(HandshakeTimeout);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                ApplicationProtocols = [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11],
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, timeout.Token);
            handshake = true;
            protocol = ssl.SslProtocol.ToString();
            alpn = ssl.NegotiatedApplicationProtocol == default ? null : ssl.NegotiatedApplicationProtocol.ToString();
            if (certificate is not null)
            {
                notAfter = certificate.NotAfter.ToUniversalTime();
                issuer = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: true) + " " + IssuerOrganization(certificate);
                problem = errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch) ? "the certificate is for another name"
                    : chainStatus.HasFlag(X509ChainStatusFlags.NotTimeValid) ? "the certificate has expired"
                    : errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors) ? "the certificate chain is not trusted"
                    : errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable) ? "no certificate"
                    : null;
                certificate.Dispose();
            }
            else
                problem = "no certificate";
        }
        catch (SafeHttpBlockedException blocked)
        {
            error = "refused: " + blocked.Reason;
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException or TimeoutException or System.Net.Sockets.SocketException
                                   or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            error = ex is AuthenticationException ? "the TLS handshake failed" : ex is OperationCanceledException or TimeoutException ? "timed out" : "no answer on port " + port;
            logger.LogDebug("TLS handshake with {Host} failed: {Message}", host, ex.Message);
        }

        var tls10 = handshake ? await LegacyAsync(host, port, LegacyTlsProbe.Tls10, cancellationToken) : (LegacyTlsAnswer?)null;
        var tls11 = handshake ? await LegacyAsync(host, port, LegacyTlsProbe.Tls11, cancellationToken) : (LegacyTlsAnswer?)null;
        return new TlsFacts(handshake, error, protocol, alpn, notAfter, problem, issuer?.Trim(), tls10, tls11, protocol == nameof(SslProtocols.Tls13));
    }

    private async Task<LegacyTlsAnswer> LegacyAsync(string host, int port, ushort version, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await sockets.ConnectAsync(SeoHttp.Tls, host, port, cancellationToken);
            return await LegacyTlsProbe.ProbeAsync(stream, host, version, HandshakeTimeout, cancellationToken);
        }
        catch (Exception ex) when (ex is SafeHttpBlockedException or IOException or TimeoutException or System.Net.Sockets.SocketException)
        {
            return LegacyTlsAnswer.Inconclusive;
        }
    }

    private static string IssuerOrganization(X509Certificate2 certificate)
    {
        foreach (var part in certificate.IssuerName.Name.Split(','))
            if (part.Trim().StartsWith("O=", StringComparison.Ordinal))
                return part.Trim()[2..].Trim('"');
        return string.Empty;
    }
}
