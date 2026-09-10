using MintPlayer.Spark.Webhooks.GitHub.DevTunnel.Services;
using System.Text;
using Xunit;

namespace MintPlayer.Spark.Tests.Webhooks.DevTunnel;

public class SmeeSseReaderTests
{
    private static async Task<List<string>> ReadAll(string stream)
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(stream));

        var frames = new List<string>();
        await foreach (var frame in SmeeSseReader.ReadFramesAsync(ms, CancellationToken.None))
            frames.Add(frame);

        return frames;
    }

    [Fact]
    public async Task Reads_one_frame_per_blank_line_delimited_event()
    {
        var frames = await ReadAll("data: {\"a\":1}\n\ndata: {\"b\":2}\n\n");

        frames.Count.Should().Be(2);
        frames[0].Should().Be("{\"a\":1}");
        frames[1].Should().Be("{\"b\":2}");
    }

    [Fact]
    public async Task Joins_a_payload_split_across_several_data_lines()
    {
        var frames = await ReadAll("data: {\"a\":\ndata: 1}\n\n");

        frames.Count.Should().Be(1);
        frames[0].Should().Be("{\"a\":\n1}");
    }

    [Fact]
    public async Task Ignores_event_id_retry_and_comment_lines()
    {
        var frames = await ReadAll(":ping\nevent: ready\nid: 7\nretry: 5000\ndata: {\"a\":1}\n\n");

        frames.Count.Should().Be(1);
        frames[0].Should().Be("{\"a\":1}");
    }

    [Fact]
    public async Task Strips_only_the_single_framing_space_after_the_colon()
    {
        // "data:  x" carries a payload that genuinely starts with a space.
        var frames = await ReadAll("data:  x\n\n");

        frames[0].Should().Be(" x");
    }

    [Fact]
    public async Task Accepts_a_data_line_with_no_space_after_the_colon()
    {
        var frames = await ReadAll("data:{\"a\":1}\n\n");

        frames[0].Should().Be("{\"a\":1}");
    }

    [Fact]
    public async Task Yields_a_trailing_frame_that_has_no_closing_blank_line()
    {
        var frames = await ReadAll("data: {\"a\":1}\n");

        frames.Count.Should().Be(1);
        frames[0].Should().Be("{\"a\":1}");
    }

    [Fact]
    public async Task Emits_nothing_for_a_stream_of_only_comments_and_blank_lines()
    {
        var frames = await ReadAll(":ping\n\n:ping\n\n");

        frames.Count.Should().Be(0);
    }

    [Fact]
    public async Task Handles_crlf_line_endings()
    {
        var frames = await ReadAll("data: {\"a\":1}\r\n\r\n");

        frames.Count.Should().Be(1);
        frames[0].Should().Be("{\"a\":1}");
    }
}
