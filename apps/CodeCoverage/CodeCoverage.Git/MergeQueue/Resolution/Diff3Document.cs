namespace CodeCoverage.Git.MergeQueue.Resolution;

/// <summary>
/// The output of <c>git merge-file -p --diff3</c> read back into its pieces (docs/coverage_merge_queue_PRD.md §10.4): runs
/// of common text and conflict regions with their ours, base and theirs sections. Every line keeps its terminator
/// (<c>\n</c> or <c>\r\n</c>) and a last line without one stays without one, so joining pieces gives back the sides'
/// exact bytes.
/// </summary>
internal sealed class Diff3Document
{
    private const string Open = "<<<<<<<", BaseMarker = "|||||||", Middle = "=======", Close = ">>>>>>>";

    private Diff3Document(IReadOnlyList<Diff3Piece> pieces) => Pieces = pieces;

    public IReadOnlyList<Diff3Piece> Pieces { get; }

    public IEnumerable<Diff3Piece.Region> Regions => Pieces.OfType<Diff3Piece.Region>();

    /// <summary>The document, or null when the markers do not form well-nested diff3 regions (each with a base
    /// section). The sides must hold no marker-like line, which the caller checks first, so every marker is git's.</summary>
    public static Diff3Document? Parse(string text)
    {
        var pieces = new List<Diff3Piece>();
        var common = new List<string>();
        List<string>? ours = null, @base = null, theirs = null, current = null;
        foreach (var line in Lines(text))
        {
            var bare = line.TrimEnd('\n').TrimEnd('\r');
            if (IsMarker(bare, Open))
            {
                if (current is not null)
                    return null;
                if (common.Count > 0)
                    pieces.Add(new Diff3Piece.Common(common));
                common = [];
                current = ours = [];
                @base = theirs = null;
            }
            else if (IsMarker(bare, BaseMarker))
            {
                if (current is null || current != ours)
                    return null;
                current = @base = [];
            }
            else if (bare == Middle)
            {
                if (current is null || current != @base)
                    return null;
                current = theirs = [];
            }
            else if (IsMarker(bare, Close))
            {
                if (current is null || current != theirs)
                    return null;
                pieces.Add(new Diff3Piece.Region(ours!, @base!, theirs!));
                current = ours = @base = theirs = null;
            }
            else
                (current ?? common).Add(line);
        }
        if (current is not null)
            return null;
        if (common.Count > 0)
            pieces.Add(new Diff3Piece.Common(common));
        return new Diff3Document(pieces);
    }

    private static bool IsMarker(string line, string marker)
        => line.StartsWith(marker, StringComparison.Ordinal) && (line.Length == marker.Length || line[marker.Length] == ' ');

    /// <summary>The lines of <paramref name="text"/>, each with its <c>\n</c>; the last one without, when the text does
    /// not end in one.</summary>
    internal static IEnumerable<string> Lines(string text)
    {
        var start = 0;
        while (start < text.Length)
        {
            var end = text.IndexOf('\n', start);
            if (end < 0)
            {
                yield return text[start..];
                yield break;
            }
            yield return text[start..(end + 1)];
            start = end + 1;
        }
    }
}

internal abstract record Diff3Piece
{
    public sealed record Common(IReadOnlyList<string> Lines) : Diff3Piece;

    public sealed record Region(IReadOnlyList<string> Ours, IReadOnlyList<string> Base, IReadOnlyList<string> Theirs) : Diff3Piece;
}
