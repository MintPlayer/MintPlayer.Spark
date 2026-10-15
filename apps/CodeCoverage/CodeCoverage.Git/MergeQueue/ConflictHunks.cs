using System.Text;
using System.Text.Json;

namespace CodeCoverage.Git.MergeQueue;

/// <summary>One conflict region of a zdiff3-style merge result: the lines of stage 2 (ours = the base branch), of stage
/// 1 (the merge base) and of stage 3 (theirs = the pull request) that git could not reconcile.</summary>
internal sealed record ConflictHunk(IReadOnlyList<string> Ours, IReadOnlyList<string> Base, IReadOnlyList<string> Theirs);

/// <summary>
/// The pieces of the hunk check (docs/coverage_merge_queue_PRD.md O2). The argument that a conflict is resolvable
/// exactly when every conflicting hunk lies on allow-listed lines:
/// <list type="number">
/// <item>The hunks are git's own: merge-tree writes the conflicted file into its result tree with conflict markers, and
/// the queue runs it with <c>merge.conflictStyle=zdiff3</c>, so each region carries all three sides. Each section is a
/// contiguous run of lines of one stage's blob: ours of stage 2, base of stage 1, theirs of stage 3. zdiff3 trims off a
/// region's edges only lines that ours and theirs share, i.e. lines both sides agree on and that are therefore not in
/// conflict; every line where they disagree stays inside. Plain <c>diff3</c> was tried first and is too coarse: for an
/// add/add conflict (empty base) it reports the whole file as one region, so even a roll-up-only add/add evicted
/// (MergeQueueResolverTests.An_add_add_conflict_on_the_roll_ups_only_resolves_to_the_base_side, red with diff3, green
/// with zdiff3). The base section is not trimmed, which can only make the check stricter.</item>
/// <item>Markers are unambiguous: every stage blob must be valid JSON (strings cannot span lines, so no valid JSON line
/// starts with <c>&lt;</c>, <c>|</c>, <c>=</c> or <c>&gt;</c>), which is also checked directly; and
/// <c>attr.tree</c> is the empty tree, so no <c>conflict-marker-size</c> attribute applies and markers are 7 wide.</item>
/// <item>A line of a stage blob is <i>allowed</i> when the only JSON construct on it is one top-level member whose key is
/// allow-listed (its name, value and trailing comma). Lines holding the root braces, another member, or nothing at all
/// are not.</item>
/// <item>Section lines are matched to their stage by text, not position. A text counts as allowed only if <b>every</b>
/// line of that stage with that text is allowed; so whichever occurrence the section actually holds, it is an allowed
/// line. A text that also appears on a forbidden line is rejected (conservative, never permissive).</item>
/// </list>
/// So: all section lines allowed ⇒ every line git reported as conflicting is an allow-listed hash line. Anything that
/// does not fit (no regions found, a malformed marker, a non-JSON stage) evicts.
/// </summary>
internal static class ConflictHunks
{
    private const string Open = "<<<<<<<", BaseMarker = "|||||||", Middle = "=======", Close = ">>>>>>>";

    /// <summary>The conflict regions of a (z)diff3-style merge result, or null when the markers do not form well-nested
    /// regions.</summary>
    public static IReadOnlyList<ConflictHunk>? Parse(string merged)
    {
        var hunks = new List<ConflictHunk>();
        List<string>? ours = null, @base = null, theirs = null, current = null;
        foreach (var raw in SplitLines(merged))
        {
            var line = raw.TrimEnd('\r');
            if (IsMarker(line, Open))
            {
                if (current is not null)
                    return null;
                current = ours = [];
                @base = null;
                theirs = null;
            }
            else if (IsMarker(line, BaseMarker))
            {
                if (current is null || current != ours)
                    return null;
                current = @base = [];
            }
            else if (line == Middle)
            {
                if (current is null || current == theirs || @base is null)
                    return null;
                current = theirs = [];
            }
            else if (IsMarker(line, Close))
            {
                if (current is null || current != theirs)
                    return null;
                hunks.Add(new ConflictHunk(ours!, @base!, theirs!));
                current = ours = @base = theirs = null;
            }
            else
                current?.Add(raw);
        }
        return current is null ? hunks : null;
    }

    /// <summary>True when a line starts like a conflict marker; such a line in a stage blob makes the regions
    /// ambiguous.</summary>
    public static bool LooksLikeMarker(string line)
        => line.StartsWith(Open, StringComparison.Ordinal) || line.StartsWith(BaseMarker, StringComparison.Ordinal)
           || line.StartsWith(Middle, StringComparison.Ordinal) || line.StartsWith(Close, StringComparison.Ordinal);

    private static bool IsMarker(string line, string marker)
        => line.StartsWith(marker, StringComparison.Ordinal) && (line.Length == marker.Length || line[marker.Length] == ' ');

    /// <summary>The lines of a text split on <c>\n</c> (a <c>\r</c> stays on its line), without the empty piece after
    /// a final newline.</summary>
    public static IEnumerable<string> SplitLines(string text)
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
            yield return text[start..end];
            start = end + 1;
        }
    }

    /// <summary>
    /// The line texts of a JSON document that are allowed (point 3 and 4 above): every line with that text holds
    /// nothing but one top-level member whose key is in <paramref name="keys"/>. Null when the text is not a single JSON
    /// object (comments and trailing commas are refused, as Spark never writes them) or has a marker-like line.
    /// </summary>
    public static IReadOnlySet<string>? AllowedLineTexts(string text, IReadOnlySet<string> keys)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var lineStarts = new List<int> { 0 };
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n' && i + 1 < bytes.Length)
                lineStarts.Add(i + 1);
        }
        var lines = SplitLines(text).ToList();
        if (lines.Count != lineStarts.Count && !(bytes.Length == 0 && lines.Count == 0))
            throw new InvalidOperationException("Line bookkeeping diverged.");
        if (lines.Any(LooksLikeMarker))
            return null;

        // owner[l]: -1 = nothing yet, -2 = more than one construct or a forbidden one, else the member index.
        var owner = Enumerable.Repeat(-1, lines.Count).ToArray();
        var allowedMember = new List<bool>();
        void Claim(int from, int to, int member)
        {
            for (var l = LineOf(from); l <= LineOf(to); l++)
                owner[l] = owner[l] == -1 || owner[l] == member ? member : -2;
        }
        int LineOf(int offset)
        {
            var index = lineStarts.BinarySearch(offset);
            return index >= 0 ? index : ~index - 1;
        }

        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return null;
            Claim((int)reader.TokenStartIndex, (int)reader.TokenStartIndex, int.MaxValue);
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var key = reader.GetString()!;
                var start = (int)reader.TokenStartIndex;
                reader.Read();
                reader.Skip();
                var member = allowedMember.Count;
                allowedMember.Add(keys.Contains(key));
                Claim(start, (int)reader.BytesConsumed - 1, member);
            }
            if (reader.TokenType != JsonTokenType.EndObject || reader.CurrentDepth != 0)
                return null;
            Claim((int)reader.TokenStartIndex, (int)reader.TokenStartIndex, int.MaxValue);
            if (reader.Read())
                return null;
        }
        catch (JsonException)
        {
            return null;
        }

        var verdict = new Dictionary<string, bool>(StringComparer.Ordinal);
        for (var l = 0; l < lines.Count; l++)
        {
            var allowed = owner[l] >= 0 && owner[l] != int.MaxValue && allowedMember[owner[l]];
            verdict[lines[l]] = verdict.TryGetValue(lines[l], out var before) ? before && allowed : allowed;
        }
        return verdict.Where(v => v.Value).Select(v => v.Key).ToHashSet(StringComparer.Ordinal);
    }
}
