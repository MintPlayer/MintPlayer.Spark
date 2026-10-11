using Microsoft.Extensions.Options;

namespace MintPlayer.Spark.Http;

/// <summary>
/// A raw TCP connection under the rules of a named SSRF-safe client (<c>docs/seo_analyzer_PRD.md</c> X2): the same
/// allowed ports, the same address allow-list after resolving, the same "connect to the address that was checked"
/// (no DNS rebinding) and the client's connect timeout. For a protocol <see cref="HttpClient"/> does not speak, such
/// as a TLS handshake probe, to a host a user chose. Registered by <c>AddSafeHttpClient</c>.
/// </summary>
public interface ISafeHttpSockets
{
    /// <summary>
    /// Connects to <paramref name="host"/>:<paramref name="port"/> as the client named <paramref name="clientName"/>
    /// would. Throws <see cref="SafeHttpBlockedException"/> when its rules refuse the port or every address.
    /// </summary>
    ValueTask<Stream> ConnectAsync(string clientName, string host, int port, CancellationToken cancellationToken);
}

internal sealed class SafeHttpSockets(IOptionsMonitor<SafeHttpOptions> options, ISafeHttpDnsResolver resolver) : ISafeHttpSockets
{
    public async ValueTask<Stream> ConnectAsync(string clientName, string host, int port, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        var named = options.Get(clientName);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(named.ConnectTimeout);
        try
        {
            return await new SafeHttpConnector(new SafeHttpPolicy(named), resolver).ConnectAsync(host, port, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Connecting to port {port} took longer than {named.ConnectTimeout}.");
        }
    }
}
