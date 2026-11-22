using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Files.ClamAV;
using MintPlayer.Spark.Files.Scanning;
using MintPlayer.Spark.Tests.Files.Stores;

namespace MintPlayer.Spark.Tests.Files.Scanning;

/// <summary>
/// #700 SP4: the clamd INSTREAM client. Every reply class maps fail-closed; the wire format is checked against a fake clamd
/// on a loopback socket, which also plays the resets and silences a real clamd produces.
/// </summary>
public class ClamAVScannerTests
{
    internal static FileScanContext Context(long length) => new(
        new EntityTypeDefinition { Id = Guid.NewGuid(), Name = "Car", ClrType = "Car" },
        new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Document", DataType = "BinaryFile" },
        "users/alice", "scan.pdf", "application/pdf", length, "00");

    [Theory]
    [InlineData("stream: OK", FileScanVerdict.Clean, null)]
    [InlineData("stream: OK\n", FileScanVerdict.Clean, null)]
    [InlineData("stream: Eicar-Test-Signature FOUND", FileScanVerdict.Infected, "Eicar-Test-Signature")]
    [InlineData("stream: Heuristics.Limits.Exceeded.MaxScanSize FOUND", FileScanVerdict.Infected, "Heuristics.Limits.Exceeded.MaxScanSize")]
    [InlineData("INSTREAM size limit exceeded. ERROR", FileScanVerdict.Failed, null)]
    [InlineData("stream: Can't allocate memory ERROR", FileScanVerdict.Failed, null)]
    [InlineData("stream:  FOUND", FileScanVerdict.Failed, null)]
    [InlineData("stream: OKAY", FileScanVerdict.Failed, null)]
    [InlineData("UNKNOWN COMMAND", FileScanVerdict.Failed, null)]
    [InlineData("", FileScanVerdict.Failed, null)]
    [InlineData(null, FileScanVerdict.Failed, null)]
    [InlineData("stream: OK (unterminated)", FileScanVerdict.Failed, null)]
    public void Every_reply_class_maps_fail_closed(string? reply, FileScanVerdict verdict, string? signature)
    {
        var result = ClamAVScanner.Parse(reply);

        result.Verdict.Should().Be(verdict);
        if (signature is not null)
            result.Detail.Should().Be(signature);
    }

    [Fact]
    public async Task The_wire_format_is_INSTREAM_with_big_endian_chunks_and_a_zero_terminator()
    {
        await using var clamd = new FakeClamd(Reply("stream: OK"));
        var payload = Enumerable.Range(0, 10_000).Select(i => (byte)i).ToArray();
        var scanner = new ClamAVScanner(new ClamAVOptions { Host = "127.0.0.1", Port = clamd.Port, ChunkSize = 4096 });

        var result = await scanner.ScanAsync(new MemoryStream(payload), Context(payload.Length), CancellationToken.None);

        result.Should().Be(FileScanResult.Clean);
        var received = await clamd.ReceivedAsync;
        received.Command.Should().Be("zINSTREAM\0");
        received.Chunks.Select(c => c.Length).Should().Equal(4096, 4096, 1808);
        received.Chunks.SelectMany(c => c).Should().Equal(payload);
        received.Terminated.Should().BeTrue();
    }

    [Fact]
    public async Task A_found_reply_is_infected()
    {
        await using var clamd = new FakeClamd(Reply("stream: Eicar-Test-Signature FOUND"));
        var scanner = new ClamAVScanner(new ClamAVOptions { Host = "127.0.0.1", Port = clamd.Port });

        var result = await scanner.ScanAsync(new MemoryStream([1, 2, 3]), Context(3), CancellationToken.None);

        result.Should().Be(FileScanResult.Infected("Eicar-Test-Signature"));
    }

    [Fact]
    public async Task A_connection_dropped_before_the_reply_is_a_failure_never_a_pass()
    {
        // What clamd does over its StreamMaxLength (SP4): it logs, and closes without a verdict.
        await using var clamd = new FakeClamd(async (_, _) => { });
        var scanner = new ClamAVScanner(new ClamAVOptions { Host = "127.0.0.1", Port = clamd.Port });

        var result = await scanner.ScanAsync(new MemoryStream(new byte[100_000]), Context(100_000), CancellationToken.None);

        result.Verdict.Should().Be(FileScanVerdict.Failed);
    }

    [Fact]
    public async Task A_clamd_that_never_answers_times_out_as_a_failure()
    {
        await using var clamd = new FakeClamd(async (_, ct) => await Task.Delay(Timeout.InfiniteTimeSpan, ct));
        var scanner = new ClamAVScanner(new ClamAVOptions { Host = "127.0.0.1", Port = clamd.Port, Timeout = TimeSpan.FromMilliseconds(500) });

        var result = await scanner.ScanAsync(new MemoryStream([1]), Context(1), CancellationToken.None);

        result.Verdict.Should().Be(FileScanVerdict.Failed);
        result.Detail.Should().Contain("did not answer");
    }

    [Fact]
    public async Task Nobody_listening_is_a_failure()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var scanner = new ClamAVScanner(new ClamAVOptions { Host = "127.0.0.1", Port = port });

        var result = await scanner.ScanAsync(new MemoryStream([1]), Context(1), CancellationToken.None);

        result.Verdict.Should().Be(FileScanVerdict.Failed);
    }

    [Fact]
    public async Task A_file_over_MaxStreamLength_is_refused_without_being_sent()
    {
        await using var clamd = new FakeClamd(Reply("stream: OK"));
        var scanner = new ClamAVScanner(new ClamAVOptions { Host = "127.0.0.1", Port = clamd.Port, MaxStreamLength = 10 });

        var declared = await scanner.ScanAsync(new MemoryStream(new byte[11]), Context(11), CancellationToken.None);
        var lying = await scanner.ScanAsync(new MemoryStream(new byte[11]), Context(5), CancellationToken.None);

        declared.Verdict.Should().Be(FileScanVerdict.Failed);
        declared.Detail.Should().Contain("not sent");
        lying.Verdict.Should().Be(FileScanVerdict.Failed, "the cap holds for the bytes read, not the declared length");
        clamd.Connections.Should().Be(1, "only the lying length got as far as a connection");
    }

    [Fact]
    public async Task The_health_probe_streams_EICAR_assembled_at_runtime()
    {
        await using var clamd = new FakeClamd(Reply("stream: Eicar-Test-Signature FOUND"));
        var scanner = new ClamAVScanner(new ClamAVOptions { Host = "127.0.0.1", Port = clamd.Port });

        var result = await scanner.ProbeAsync(CancellationToken.None);

        result.Verdict.Should().Be(FileScanVerdict.Infected);
        var sent = Encoding.ASCII.GetString((await clamd.ReceivedAsync).Chunks.SelectMany(c => c).ToArray());
        sent.Should().HaveLength(68).And.StartWith("X5O!P%@AP").And.EndWith("$H+H*");
    }

    [Fact]
    public void Nonsensical_options_are_refused()
    {
        var act = () => new ClamAVScanner(new ClamAVOptions { Host = "", Port = 0, MaxStreamLength = 0 });

        act.Should().Throw<ArgumentException>().WithMessage("*Host*Port*MaxStreamLength*");
    }

    private static Func<Received, CancellationToken, Task> Reply(string reply)
        => (received, _) => received.Stream.WriteAsync(Encoding.ASCII.GetBytes(reply + "\0")).AsTask();

    internal sealed record Received(NetworkStream Stream, string Command, List<byte[]> Chunks, bool Terminated);

    /// <summary>A one-shot clamd on loopback: reads one INSTREAM request, then lets the test decide what to answer.</summary>
    private sealed class FakeClamd : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly TaskCompletionSource<Received> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task loop;
        private int connections;

        public FakeClamd(Func<Received, CancellationToken, Task> answer)
        {
            listener.Start();
            loop = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token);
                    Interlocked.Increment(ref connections);
                    await using var stream = client.GetStream();
                    var request = await ReadAsync(stream, stop.Token);
                    received.TrySetResult(request);
                    await answer(request, stop.Token);
                }
            });
        }

        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        public int Connections => Volatile.Read(ref connections);

        public Task<Received> ReceivedAsync => received.Task.WaitAsync(TimeSpan.FromSeconds(10));

        private static async Task<Received> ReadAsync(NetworkStream stream, CancellationToken ct)
        {
            var command = new byte[10];
            await stream.ReadExactlyAsync(command, ct);
            var chunks = new List<byte[]>();
            var length = new byte[4];
            while (true)
            {
                await stream.ReadExactlyAsync(length, ct);
                var size = BinaryPrimitives.ReadUInt32BigEndian(length);
                if (size == 0)
                    return new Received(stream, Encoding.ASCII.GetString(command), chunks, true);
                var chunk = new byte[size];
                await stream.ReadExactlyAsync(chunk, ct);
                chunks.Add(chunk);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            listener.Stop();
            try
            {
                await loop;
            }
            catch (Exception)
            {
                // Cancelled accept or a client the test already let go: the fake's own teardown.
            }
            stop.Dispose();
        }
    }
}

/// <summary>
/// Against a real clamd (CI starts <c>clamav/clamav-debian</c> with the package's <c>clamd.conf</c>):
/// <c>SPARK_CLAMAV_HOST</c> (host or host:port). The SP4 cases: EICAR, clean, the over-limit reset, the AlertExceedsMax zip.
/// </summary>
[StoreGate(Variable)]
public class ClamAVIntegrationTests
{
    internal const string Variable = "SPARK_CLAMAV_HOST";

    private static ClamAVScanner Scanner(long maxStreamLength = 25L * 1024 * 1024)
    {
        var value = Environment.GetEnvironmentVariable(Variable)!;
        var parts = value.Split(':');
        return new ClamAVScanner(new ClamAVOptions
        {
            Host = parts[0],
            Port = parts.Length > 1 ? int.Parse(parts[1]) : 3310,
            MaxStreamLength = maxStreamLength,
            Timeout = TimeSpan.FromSeconds(60),
        });
    }

    [StoreFact]
    public async Task EICAR_is_infected_and_the_health_probe_passes()
    {
        var result = await Scanner().ProbeAsync(CancellationToken.None);

        result.Verdict.Should().Be(FileScanVerdict.Infected);
        result.Detail.Should().Contain("Eicar");
    }

    [StoreFact]
    public async Task A_clean_file_is_clean()
    {
        var bytes = FileSnifferTests.Fixture("scan.pdf");

        var result = await Scanner().ScanAsync(new MemoryStream(bytes), ClamAVScannerTests.Context(bytes.Length), CancellationToken.None);

        result.Should().Be(FileScanResult.Clean);
    }

    [StoreFact]
    public async Task A_stream_over_clamds_StreamMaxLength_is_a_failure_not_a_pass()
    {
        // The client's own cap is raised above clamd's 25M, so clamd's reset is what the client sees (SP4).
        var bytes = new byte[30 * 1024 * 1024];
        Random.Shared.NextBytes(bytes);

        var result = await Scanner(maxStreamLength: 100L * 1024 * 1024).ScanAsync(new MemoryStream(bytes), ClamAVScannerTests.Context(bytes.Length), CancellationToken.None);

        result.Verdict.Should().Be(FileScanVerdict.Failed);
    }

    [StoreFact]
    public async Task An_archive_hiding_an_over_limit_file_is_infected_with_AlertExceedsMax()
    {
        // 150 MiB of zeros compresses to about 150 KiB: a small upload whose member is over MaxScanSize (100M). ClamAV 1.5
        // trims a member to MaxFileSize without an alert, so this alerts only because clamd.conf keeps MaxFileSize >= MaxScanSize.
        const int InnerMiB = 150;
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = archive.CreateEntry("big.bin", CompressionLevel.SmallestSize).Open();
            var zeros = new byte[1024 * 1024];
            for (var i = 0; i < InnerMiB; i++)
                entry.Write(zeros);
        }
        zip.Position = 0;

        var result = await Scanner().ScanAsync(zip, ClamAVScannerTests.Context(zip.Length), CancellationToken.None);

        result.Verdict.Should().Be(FileScanVerdict.Infected);
        result.Detail.Should().StartWith("Heuristics.Limits.Exceeded");
    }
}
