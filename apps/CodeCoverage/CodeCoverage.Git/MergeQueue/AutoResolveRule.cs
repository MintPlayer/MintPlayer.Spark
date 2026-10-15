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
            throw new ArgumentException("An auto-resolve rule needs at least one key: with none, every conflict evicts anyway. Use WholeFile for a file whose every line may conflict.", nameof(keys));
        pattern = new Regex(GlobToRegex(pathGlob), RegexOptions.CultureInvariant | RegexOptions.Singleline);
    }

    private AutoResolveRule(string pathGlob)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathGlob);
        PathGlob = pathGlob;
        Keys = new HashSet<string>(StringComparer.Ordinal);
        AnyLine = true;
        pattern = new Regex(GlobToRegex(pathGlob), RegexOptions.CultureInvariant | RegexOptions.Singleline);
    }

    /// <summary>A rule whose every line may conflict: the base branch's version of the whole file is taken, without the
    /// O2 hunk check and without requiring JSON (open question Q7: the owner may widen the allow-list to whole files).
    /// The other limits still hold: both sides a regular, non-executable file, each side within the size cap, and only
    /// <c>CONFLICT (contents)</c>/<c>CONFLICT (add/add)</c>.</summary>
    public static AutoResolveRule WholeFile(string pathGlob) => new(pathGlob);

    /// <summary>Spark's roll-up hash lines (O2). Per-file and per-entity hashes are NOT on it: two pull requests that
    /// change the same entry changed the same model input, which is a real conflict.</summary>
    public static AutoResolveRule SparkModelHashes { get; } = new("**/App_Data/modelHashes.json", ["modelHash", "contextRoots", "modelFiles"]);

    /// <summary>The default allow-list since O19 (Q7 → A): Spark's wholly generated files, where a conflict on ANY line is
    /// resolved with take-base, because the regenerate step rewrites them. This supersedes <see cref="SparkModelHashes"/>
    /// as the default; the key-scoped rule stays for repositories that want the narrower O2 check.</summary>
    public static IReadOnlyList<AutoResolveRule> SparkGeneratedFiles { get; } =
    [
        WholeFile("**/App_Data/modelHashes.json"),
        WholeFile("**/App_Data/securityPosture.txt"),
    ];

    /// <summary>A repository-relative path glob: <c>**</c> spans directories, <c>*</c> and <c>?</c> stay within one;
    /// case-sensitive, like git paths.</summary>
    public string PathGlob { get; }

    /// <summary>The top-level JSON members whose lines may conflict; empty when <see cref="AnyLine"/>.</summary>
    public IReadOnlySet<string> Keys { get; }

    /// <summary>Any line of the file may conflict (<see cref="WholeFile"/>).</summary>
    public bool AnyLine { get; }

    /// <summary>The conflict strategy (§10.3), one of <see cref="MergeQueueWireRules.Strategies"/>; <c>take-base</c> for
    /// the key-scoped and whole-file rules.</summary>
    public string Strategy { get; private init; } = MergeQueueWireRules.TakeBase;

    /// <summary>The validators the resolved content must pass (§10.4 step 4), besides the always-on
    /// <c>no-conflict-markers</c>; also the ones a cleanly merged file under this rule must pass.</summary>
    public IReadOnlyList<string> Validate { get; private init; } = [];

    /// <summary>Whether a resolution under this rule needs the regenerate workflow; null leaves it to the strategy's
    /// default (take-base: yes).</summary>
    public bool? Regenerate { get; private init; }

    /// <summary>A rule with a strategy other than the key-scoped or whole-file take-base: <c>keep-both</c>,
    /// <c>line-set</c> or <c>evict</c>, or a whole-file <c>take-base</c> with validators or a regenerate choice.</summary>
    public static AutoResolveRule Create(string pathGlob, string strategy, IEnumerable<string>? validate = null, bool? regenerate = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategy);
        return new AutoResolveRule(pathGlob)
        {
            Strategy = strategy,
            Validate = [.. validate ?? []],
            Regenerate = regenerate,
        };
    }

    /// <summary>This rule with validators and a regenerate choice (the key-scoped take-base rule keeps its keys).</summary>
    public AutoResolveRule With(IEnumerable<string>? validate, bool? regenerate)
        => new(this) { Validate = [.. validate ?? []], Regenerate = regenerate };

    private AutoResolveRule(AutoResolveRule other)
    {
        pattern = other.pattern;
        PathGlob = other.PathGlob;
        Keys = other.Keys;
        AnyLine = other.AnyLine;
        Strategy = other.Strategy;
        Validate = other.Validate;
        Regenerate = other.Regenerate;
    }

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
