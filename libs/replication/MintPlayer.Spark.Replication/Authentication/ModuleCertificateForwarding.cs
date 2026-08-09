using System.Net;
using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Replication.Authentication;

/// <summary>
/// Where the client certificate comes from when TLS is terminated before the app.
/// </summary>
public sealed class SparkCertificateForwardingOptions
{
    /// <summary>
    /// The header the proxy puts the client certificate in. Defaults to <c>X-ARR-ClientCert</c>
    /// (Azure App Service, IIS ARR).
    /// <para>
    /// Traefik — the proxy this repository's own compose file uses — sends
    /// <c>X-Forwarded-Tls-Client-Cert</c> when <c>passTLSClientCert</c> is enabled. nginx sends
    /// <c>ssl-client-cert</c>. There is no standard here, which is exactly why this is configurable
    /// rather than assumed.
    /// </para>
    /// </summary>
    public string HeaderName { get; set; } = "X-ARR-ClientCert";

    /// <summary>
    /// The proxies permitted to assert a client certificate. **Required.**
    /// <para>
    /// This has no default and refuses to start empty, because the safe value cannot be guessed and
    /// the unsafe one is invisible: a forwarded certificate is a plain request header, so any
    /// caller that can reach the app directly can claim to be any module simply by setting it.
    /// Every demo in this repository calls <c>KnownProxies.Clear()</c> for
    /// <c>X-Forwarded-For</c>, which is tolerable for a client IP and catastrophic for an
    /// authentication credential. Inheriting that posture here would convert mTLS into a header
    /// anyone can write.
    /// </para>
    /// </summary>
    public List<IPAddress> KnownProxies { get; } = [];
}

public static class SparkCertificateForwardingExtensions
{
    /// <summary>
    /// Reads the client certificate from a proxy header instead of the TLS connection, for
    /// deployments where TLS terminates at the edge.
    /// <para>
    /// Without this, mTLS is structurally impossible behind a terminating proxy — <c>
    /// HttpContext.Connection.ClientCertificate</c> is always null there, so every cross-module call
    /// fails authentication no matter how correctly the certificates are configured (F3). The
    /// operator's escape from that dead end was to switch the mode to <c>Development</c> or
    /// <c>Disabled</c>, which is a short path from "following the documentation" to "authentication
    /// off".
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// If no trusted proxy is configured. Failing to start is the intended behaviour: an app that
    /// accepted a certificate header from anyone would authenticate attackers as modules, and would
    /// do it silently.
    /// </exception>
    public static ISparkBuilder AddModuleCertificateForwarding(
        this ISparkBuilder builder,
        Action<SparkCertificateForwardingOptions> configure)
    {
        var options = new SparkCertificateForwardingOptions();
        configure(options);

        if (options.KnownProxies.Count == 0)
        {
            throw new InvalidOperationException(
                "AddModuleCertificateForwarding requires at least one entry in KnownProxies. A "
                + "forwarded client certificate is an ordinary request header, so accepting it from "
                + "any source lets any caller authenticate as any module. Configure the address of "
                + "the terminating proxy, or do not enable forwarding.");
        }

        builder.Services.AddCertificateForwarding(forwarding =>
        {
            forwarding.CertificateHeader = options.HeaderName;
        });

        var trusted = options.KnownProxies.ToArray();

        builder.Registry.AddMiddleware(app =>
        {
            // Strip the header from anything that is not the trusted proxy, *before* the forwarding
            // middleware reads it. Ordering is the whole control: the check has to happen upstream
            // of the component that trusts the value, not alongside it.
            app.Use(async (context, next) =>
            {
                var remote = context.Connection.RemoteIpAddress;
                var fromTrustedProxy = remote is not null
                    && trusted.Any(p => p.Equals(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote));

                if (!fromTrustedProxy)
                    context.Request.Headers.Remove(options.HeaderName);

                await next(context);
            });

            app.UseCertificateForwarding();
        });

        return builder;
    }
}
