using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Webhooks.GitHub.DevTunnel.Configuration;
using MintPlayer.Spark.Webhooks.GitHub.DevTunnel.Services;
using System.Net;
using Xunit;

namespace MintPlayer.Spark.Tests.Webhooks.DevTunnel;

/// <summary>
/// Start/stop behaviour of the smee tunnel. The previous implementation exited permanently on the
/// first failure — <c>StartAsync</c> was awaited once and the <c>BackgroundService</c> was done —
/// so a momentary blip left the developer with a tunnel that looked alive and delivered nothing.
/// </summary>
public class SmeeBackgroundServiceLifecycleTests
{
    /// <summary>A response whose body never ends, so the read loop stays parked until cancelled.</summary>
    private sealed class NeverEndingStreamHandler : HttpMessageHandler
    {
        public int Attempts;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Attempts);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new BlockingStream(cancellationToken)),
            });
        }
    }

    /// <summary>Blocks on read until the token is cancelled — a live SSE channel with no traffic.</summary>
    private sealed class BlockingStream(CancellationToken token) : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
            await Task.Delay(Timeout.Infinite, linked.Token);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static SmeeBackgroundService Build(string channelUrl, HttpMessageHandler handler)
        // Parameter order follows the source-generated constructor: [Inject] fields, then [Options].
        => new(
            new SingleClientFactory(handler),
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<SmeeBackgroundService>.Instance,
            Options.Create(new SmeeOptions { ChannelUrl = channelUrl }));

    [Fact]
    public async Task Stopping_the_host_completes_without_throwing()
    {
        using var handler = new NeverEndingStreamHandler();
        var service = Build("https://smee.io/test-channel", handler);

        await service.StartAsync(CancellationToken.None);

        // The bug this pins: a Task.Delay on the stopping token, or a cancelled read, surfacing as
        // an unhandled TaskCanceledException out of ExecuteAsync when the host shuts down.
        await service.StopAsync(CancellationToken.None);

        service.ExecuteTask.Should().NotBeNull();
        service.ExecuteTask!.IsFaulted.Should().BeFalse();
    }

    [Fact]
    public async Task Does_nothing_when_no_channel_is_configured()
    {
        using var handler = new NeverEndingStreamHandler();
        var service = Build(string.Empty, handler);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        handler.Attempts.Should().Be(0);
    }
}
