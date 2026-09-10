namespace MintPlayer.Spark.Webhooks.GitHub.DevTunnel.Services;

/// <summary>
/// Minimal Server-Sent Events reader for a smee.io channel.
/// <para>
/// Split out from <see cref="SmeeBackgroundService"/> so the framing can be tested against a plain
/// <see cref="Stream"/> without a live channel. Only the parts of the SSE grammar smee actually
/// uses are implemented: <c>data:</c> lines accumulate, a blank line dispatches the frame, and
/// everything else (<c>event:</c>, <c>id:</c>, <c>retry:</c>, <c>:</c> comments) is ignored.
/// </para>
/// </summary>
internal static class SmeeSseReader
{
    /// <summary>
    /// Yields one string per SSE event: the concatenated <c>data:</c> payload, newline-joined when
    /// the sender split it across lines. Completes when the stream ends.
    /// </summary>
    public static async IAsyncEnumerable<string> ReadFramesAsync(
        Stream stream,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream);
        var dataLines = new List<string>();

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                if (dataLines.Count > 0)
                {
                    var frame = string.Join('\n', dataLines);
                    dataLines.Clear();
                    yield return frame;
                }

                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                // A single optional space after the colon is part of the framing, not the payload.
                var value = line[5..];
                dataLines.Add(value.StartsWith(' ') ? value[1..] : value);
            }
        }

        // A stream that ends without a trailing blank line still carries a complete frame.
        if (dataLines.Count > 0)
            yield return string.Join('\n', dataLines);
    }
}
