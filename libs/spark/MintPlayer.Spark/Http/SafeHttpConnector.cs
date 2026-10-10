using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;

namespace MintPlayer.Spark.Http;

/// <summary>Resolves a host name for the SSRF-safe client. A seam for tests (DNS rebinding); the default is the system resolver.</summary>
internal interface ISafeHttpDnsResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

internal sealed class SystemDnsResolver : ISafeHttpDnsResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
        => Dns.GetHostAddressesAsync(host, cancellationToken);
}

/// <summary>
/// The <see cref="SocketsHttpHandler.ConnectCallback"/> of the SSRF-safe client: resolves the host itself,
/// keeps only the addresses <see cref="SafeHttpPolicy"/> allows, and connects to one of <b>those</b>
/// addresses. Checking and connecting use the same resolution, so a DNS answer that changes between a
/// check and a connect (DNS rebinding) has nothing to change.
/// </summary>
internal sealed class SafeHttpConnector(
    SafeHttpPolicy policy,
    ISafeHttpDnsResolver resolver,
    Func<IPAddress, int, CancellationToken, ValueTask<Stream>>? connect = null)
{
    private readonly Func<IPAddress, int, CancellationToken, ValueTask<Stream>> connect = connect ?? ConnectSocketAsync;

    public ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
        => ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, cancellationToken);

    public async ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        if (!policy.IsPortAllowed(port))
            throw new SafeHttpBlockedException(SafeHttpBlockReason.Port);

        IPAddress[] candidates;
        if (IPAddress.TryParse(host.TrimStart('[').TrimEnd(']'), out var literal))
            candidates = [literal];
        else
        {
            try
            {
                candidates = await resolver.ResolveAsync(host, cancellationToken);
            }
            catch (SocketException)
            {
                throw new SafeHttpBlockedException(SafeHttpBlockReason.Resolution);
            }
        }

        if (candidates.Length == 0)
            throw new SafeHttpBlockedException(SafeHttpBlockReason.Resolution);

        // Only vetted addresses are ever dialled. An answer that mixes public and private addresses
        // therefore cannot steer the connection to the private one.
        var vetted = candidates.Where(policy.IsAddressAllowed).ToArray();
        if (vetted.Length == 0)
            throw new SafeHttpBlockedException(SafeHttpBlockReason.Address);

        ExceptionDispatchInfo? last = null;
        foreach (var address in vetted)
        {
            try
            {
                return await connect(address, port, cancellationToken);
            }
            catch (SocketException ex)
            {
                last = ExceptionDispatchInfo.Capture(ex);
            }
        }
        last!.Throw();
        throw new UnreachableException();
    }

    private static async ValueTask<Stream> ConnectSocketAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
