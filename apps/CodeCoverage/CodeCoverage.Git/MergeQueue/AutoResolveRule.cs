using System.Text;
using System.Text.RegularExpressions;

namespace CodeCoverage.Git.MergeQueue;

/// <summary>
/// One allow-list entry (docs/coverage_merge_queue_PRD.md O1, O2): a path glob and the JSON top-level keys whose lines
/// may conflict. The only resolution is <b>take-base</b> (the base branch's version of the file; the post-rebase
/// regeneration then rewrites it). For Spark's <c>App_Data/modelHashes.json</c> the keys are the roll-ups
/// <c>modelHash</c>, <c>contextRoots</c> and <c>modelFiles</c> (<see cref="SparkModelHashes"/>).
/// </summary>
public sealed class AutoResolveRule
{
    private readonly Regex pattern;

    public AutoResolveRule(string pathGlob, IEnumerable<string> keys)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathGlob);
        PathGlob = pathGlob;
        Keys = keys.ToHashSet(StringComparer.Ordinal);
        if (Keys.Count == 0)
            throw new ArgumentException("An auto-resolve rule needs at least one key: with none, every conflict evicts anyway.", nameof(keys));
        pattern = new Regex(GlobToRegex(pathGlob), RegexOptions.CultureInvariant | RegexOptions.Singleline);
    }

    /// <summary>Spark's roll-up hash lines (O2). Per-file and per-entity hashes are NOT on it: two pull requests that
    /// change the same entry changed the same model input, which is a real conflict.</summary>
    public static AutoResolveRule SparkModelHashes { get; } = new("**/App_Data/modelHashes.json", ["modelHash", "contextRoots", "modelFiles"]);

    /// <summary>A repository-relative path glob: <c>**</c> spans directories, <c>*</c> and <c>?</c> stay within one;
    /// case-sensitive, like git paths.</summary>
    public string PathGlob { get; }

    public IReadOnlySet<string> Keys { get; }

    public bool Matches(string path) => pattern.IsMatch(path);

    internal static string GlobToRegex(string glob)
    {
        var regex = new StringBuilder(@"\A");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                var slash = i + 2 < glob.Length && glob[i + 2] == '/';
                regex.Append(slash ? "(?:[^/]+/)*" : ".*");
                i += slash ? 2 : 1;
            }
            else if (c == '*')
                regex.Append("[^/]*");
            else if (c == '?')
                regex.Append("[^/]");
            else
                regex.Append(Regex.Escape(c.ToString()));
        }
        return regex.Append(@"\z").ToString();
    }
}
