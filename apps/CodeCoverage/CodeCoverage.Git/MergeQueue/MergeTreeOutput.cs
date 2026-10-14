using System.Globalization;
using System.Text.RegularExpressions;

namespace CodeCoverage.Git.MergeQueue;

/// <summary>One conflicted index entry: <c>&lt;mode&gt; &lt;object&gt; &lt;stage&gt;\t&lt;path&gt;</c>. Stage 1 is the
/// merge base's version, stage 2 the first commit given to merge-tree (the queue passes the base branch there) and
/// stage 3 the second (the pull request's head).</summary>
public sealed record MergeTreeStage(string Mode, string ObjectId, int Stage, string Path);

/// <summary>One informational message. <see cref="Type"/> is git's stable conflict-type string (<c>Auto-merging</c>,
/// <c>CONFLICT (contents)</c>, <c>CONFLICT (modify/delete)</c>, …); <see cref="Text"/> is for humans and may change
/// between git versions, so nothing decides on it.</summary>
public sealed record MergeTreeMessage(IReadOnlyList<string> Paths, string Type, string Text);

/// <summary>
/// The output of <c>git merge-tree --write-tree -z --messages</c> (git-merge-tree(1), "OUTPUT"): the toplevel tree OID,
/// then the conflicted file info, an empty field, then the informational messages, every field NUL-terminated and
/// every path verbatim (no quoting under <c>-z</c>). A clean merge prints the tree and the empty separator; messages
/// may follow it all the same.
/// </summary>
public sealed partial record MergeTreeOutput(string Tree, IReadOnlyList<MergeTreeStage> Stages, IReadOnlyList<MergeTreeMessage> Messages)
{
    [GeneratedRegex(@"\A([0-7]{6}) ([0-9a-f]{40}) ([123])\t(.+)\z", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex StageEntry();

    [GeneratedRegex(@"\A[0-9a-f]{40}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ObjectId();

    /// <summary>Parses the output strictly: anything that is not exactly this shape throws, because a resolver that
    /// guesses at its input could resolve a conflict it did not understand.</summary>
    public static MergeTreeOutput Parse(string output)
    {
        if (output.Length == 0 || output[^1] != '\0')
            throw new FormatException("merge-tree -z output must end with a NUL.");
        var fields = output[..^1].Split('\0');
        var i = 0;

        var tree = fields[i++];
        if (!ObjectId().IsMatch(tree))
            throw new FormatException("merge-tree output does not start with a tree OID.");

        var stages = new List<MergeTreeStage>();
        while (i < fields.Length && fields[i].Length > 0)
        {
            var match = StageEntry().Match(fields[i++]);
            if (!match.Success)
                throw new FormatException("A conflicted file info entry does not parse.");
            stages.Add(new MergeTreeStage(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value[0] - '0', match.Groups[4].Value));
        }
        if (i < fields.Length)
            i++; // the empty separator field

        var messages = new List<MergeTreeMessage>();
        while (i < fields.Length)
        {
            if (!int.TryParse(fields[i++], NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 1 || i + count + 2 > fields.Length)
                throw new FormatException("A message's path count does not parse.");
            var paths = fields[i..(i + count)];
            i += count;
            var type = fields[i++];
            var text = fields[i++];
            messages.Add(new MergeTreeMessage(paths, type, text));
        }
        return new MergeTreeOutput(tree, stages, messages);
    }
}
