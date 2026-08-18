using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Replication.Authentication;

namespace MintPlayer.Spark.Tests.Authentication;

/// <summary>
/// M10.2 — whether a forwarded client certificate is believed depends on who forwarded it.
/// <para>
/// This is the assertion the whole feature rests on. A forwarded certificate is an ordinary request
/// header, so trusting it unconditionally converts mTLS into a header anyone can write — an
/// attacker reaching the app directly could claim to be any module. The startup guard refuses an
/// empty allowlist; these tests prove the allowlist is then actually applied to traffic, which is a
/// different question and the one that matters at runtime.
/// </para>
/// </summary>
public class CertificateForwardingTrustTests
{
    private const string Header = "X-Forwarded-Tls-Client-Cert";
    private static readonly IPAddress TrustedProxy = IPAddress.Parse("10.0.0.7");

    /// <summary>
    /// Runs the middleware the builder registered, with <paramref name="remoteIp"/> as the peer, and
    /// reports whether the certificate header survived to the endpoint.
    /// </summary>
    private static async Task<bool> HeaderSurvivesAsync(IPAddress? remoteIp)
    {
        var builder = new SparkBuilder(new ServiceCollection());
        builder.AddModuleCertificateForwarding(o =>
        {
            o.HeaderName = Header;
            o.KnownProxies.Add(TrustedProxy);
        });

        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    foreach (var descriptor in builder.Services)
                        services.Add(descriptor);
                })
                .Configure(app =>
                {
                    // TestServer leaves RemoteIpAddress unset, so the peer is stamped here — before
                    // the registered middleware runs, which is where the trust decision is made.
                    app.Use(async (context, next) =>
                    {
                        context.Connection.RemoteIpAddress = remoteIp;
                        await next(context);
                    });

                    builder.Registry.ApplyMiddleware(app, SparkMiddlewareStage.AfterSpark);

                    app.Run(async context =>
                        await context.Response.WriteAsync(
                            context.Request.Headers.ContainsKey(Header).ToString()));
                }))
            .StartAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        // A syntactically valid but irrelevant value: the middleware's job is to decide whether the
        // header is believed at all, which it must do without parsing it.
        request.Headers.Add(Header, "MIIBogus");

        var response = await host.GetTestClient().SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        return bool.Parse(body);
    }

    [Fact]
    public async Task A_certificate_header_from_the_trusted_proxy_is_kept()
    {
        (await HeaderSurvivesAsync(TrustedProxy)).Should().BeTrue(
            "this is the deployment the feature exists for — TLS terminates at the proxy, so the "
            + "certificate can only arrive as a header");
    }

    /// <summary>
    /// The security property. Without this, mTLS behind a proxy is worse than no mTLS: it looks
    /// like authentication while accepting a self-asserted identity from anyone.
    /// </summary>
    [Fact]
    public async Task A_certificate_header_from_anyone_else_is_stripped()
    {
        (await HeaderSurvivesAsync(IPAddress.Parse("203.0.113.9"))).Should().BeFalse(
            "a caller reaching the app directly must not be able to assert a client certificate");
    }

    /// <summary>
    /// An unknown peer is not a trusted one. Absence of information is not evidence of trust — the
    /// same fail-closed reading the rest of this codebase applies to unevaluable state.
    /// </summary>
    [Fact]
    public async Task A_certificate_header_from_an_unknown_peer_is_stripped()
    {
        (await HeaderSurvivesAsync(remoteIp: null)).Should().BeFalse(
            "if the peer cannot be identified, it cannot be the trusted proxy");
    }
}
