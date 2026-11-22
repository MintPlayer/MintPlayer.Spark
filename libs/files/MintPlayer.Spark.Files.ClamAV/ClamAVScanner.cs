using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using MintPlayer.Spark.Files.Scanning;

namespace MintPlayer.Spark.Files.ClamAV;

/// <summary>Where clamd listens, and the limits this client holds it to.</summary>
public sealed class ClamAVOptions
{
    /// <summary>clamd's host name. Keep clamd on an internal network only: it has no authentication.</summary>
    public string Host { get; set; } = "localhost";

    /// <summary>clamd's <c>TCPSocket</c>. Default 3310.</summary>
    public int Port { get; set; } = 3310;

    /// <summary>
    /// The largest file this client streams. Default 25 MiB, clamd's default <c>StreamMaxLength</c>. Keep it at most
    /// clamd's <c>StreamMaxLength</c> and at least <c>Spark:Files:MaxFileBytes</c>: a larger file is refused without being
    /// sent (clamd would drop the connection instead of answering, PRD SP4).
    /// </summary>
    public long MaxStreamLength { get; set; } = 25L * 1024 * 1024;

    /// <summary>How long one scan may take, connect included. Default 60 s. A timeout is a failed scan, never a pass.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>The INSTREAM chunk size. Default 64 KiB.</summary>
    public int ChunkSize { get; set; } = 64 * 1024;

    internal IEnumerable<string> Problems()
    {
        if (string.IsNullOrWhiteSpace(Host))
            yield return "ClamAV Host is required.";
        if (Port is <= 0 or > 65535)
            yield return "ClamAV Port must be between 1 and 65535.";
        if (MaxStreamLength <= 0)
            yield return "ClamAV MaxStreamLength must be positive.";
        if (Timeout <= TimeSpan.Zero)
            yield return "ClamAV Timeout must be positive.";
        if (ChunkSize is <= 0 or > 1024 * 1024)
            yield return "ClamAV ChunkSize must be between 1 byte and 1 MiB.";
    }
}

/// <summary>
/// Our own clamd <c>INSTREAM</c> client (PRD SP4: no package, exact control over what counts as a failure). It maps the
/// replies fail-closed: <c>OK</c> is clean; any <c>FOUND</c> (with <c>AlertExceedsMax yes</c>, also
/// <c>Heuristics.Limits.Exceeded.*</c>) is infected; <c>ERROR</c>, an unreadable reply, a reset or a timeout is a failure.
/// </summary>
public sealed class ClamAVScanner : IFileScanner
{
    private static readonly byte[] Command = "zINSTREAM\0"u8.ToArray();

    public ClamAVScanner(ClamAVOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var problems = options.Problems().ToList();
        if (problems.Count > 0)
            throw new ArgumentException(string.Join(" ", problems), nameof(options));
        Options = options;
    }

    public ClamAVOptions Options { get; }

    public async Task<FileScanResult> ScanAsync(Stream content, FileScanContext context, CancellationToken cancellationToken)
    {
        if (context.Length > Options.MaxStreamLength)
            return FileScanResult.Failed($"The file ({context.Length} bytes) is larger than the ClamAV MaxStreamLength ({Options.MaxStreamLength}); it was not sent.");
        return await ScanStreamAsync(content, cancellationToken);
    }

    /// <summary>
    /// A health probe: streams the EICAR test string and expects <c>FOUND</c>. A clamd without (or with broken) signatures
    /// still answers <c>PING</c>, so a PING proves nothing (SP4).
    /// </summary>
    public async Task<FileScanResult> ProbeAsync(CancellationToken cancellationToken)
    {
        using var eicar = new MemoryStream(Eicar.Bytes());
        return await ScanStreamAsync(eicar, cancellationToken);
    }

    private async Task<FileScanResult> ScanStreamAsync(Stream content, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Options.Timeout);
        var ct = timeout.Token;
        try
        {
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(Options.Host, Options.Port, ct);
            await using var network = client.GetStream();

            await network.WriteAsync(Command, ct);
            var frame = new byte[4 + Options.ChunkSize];
            long sent = 0;
            int read;
            while ((read = await content.ReadAsync(frame.AsMemory(4, Options.ChunkSize), ct)) > 0)
            {
                sent += read;
                // The declared length can lie; the cap holds for the bytes actually read.
                if (sent > Options.MaxStreamLength)
                    return FileScanResult.Failed($"The stream is longer than the ClamAV MaxStreamLength ({Options.MaxStreamLength}).");
                BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)read);
                await network.WriteAsync(frame.AsMemory(0, 4 + read), ct);
            }
            await network.WriteAsync(new byte[4], ct);

            return Parse(await ReadReplyAsync(network, ct));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return FileScanResult.Failed($"clamd did not answer within {Options.Timeout}.");
        }
        catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
        {
            // clamd drops the connection on its own size limit instead of answering: a reset is never a pass.
            return FileScanResult.Failed($"The connection to clamd failed: {ex.Message}");
        }
    }

    private static async Task<string?> ReadReplyAsync(NetworkStream network, CancellationToken ct)
    {
        var reply = new List<byte>(64);
        var buffer = new byte[256];
        while (reply.Count < 4096)
        {
            var read = await network.ReadAsync(buffer, ct);
            if (read == 0)
                break;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == 0)
                    return Encoding.ASCII.GetString(reply.ToArray());
                reply.Add(buffer[i]);
            }
        }
        // EOF or an endless reply without its terminator: not a reply this client trusts.
        return reply.Count == 0 ? null : Encoding.ASCII.GetString(reply.ToArray()) + " (unterminated)";
    }

    /// <summary>Maps one clamd reply (<c>stream: OK</c>, <c>stream: &lt;name&gt; FOUND</c>, <c>… ERROR</c>) to a verdict.</summary>
    internal static FileScanResult Parse(string? reply)
    {
        if (reply is null)
            return FileScanResult.Failed("clamd closed the connection without a reply.");
        var text = reply.Trim();
        if (text.EndsWith("(unterminated)", StringComparison.Ordinal))
            return FileScanResult.Failed($"clamd's reply was cut off: '{text}'.");
        const string prefix = "stream: ";
        if (!text.StartsWith(prefix, StringComparison.Ordinal))
            return FileScanResult.Failed($"clamd answered '{text}'.");
        var body = text[prefix.Length..];
        if (body == "OK")
            return FileScanResult.Clean;
        if (body.EndsWith(" FOUND", StringComparison.Ordinal) && body.Length > " FOUND".Length)
            return FileScanResult.Infected(body[..^" FOUND".Length]);
        return FileScanResult.Failed($"clamd answered '{text}'.");
    }
}

/// <summary>
/// The EICAR anti-malware test file, assembled at runtime from parts kept apart in the assembly, so no file in the repository
/// or the build output carries the signature and no scanner ever flags our own binaries (SP4).
/// </summary>
internal static class Eicar
{
    private static readonly string[] Parts = ["X5O!P%@AP[4\\PZX54(P^)7CC)7}$", "EICAR-STANDARD-", "ANTIVIRUS-", "TEST-FILE!$H+H*"];

    public static byte[] Bytes() => Encoding.ASCII.GetBytes(string.Concat(Parts));
}
