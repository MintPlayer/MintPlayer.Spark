using System.Globalization;
using System.Text.RegularExpressions;

namespace CodeCoverage.Git.MergeQueue;

/// <summary>
/// The rules every value the merge queue sends its git writer must meet (docs/coverage_merge_queue_PRD.md §0 D57), in one
/// place: the writer refuses a request that breaks them (<c>WireValidation</c>, a 400 naming the field), and the app holds
/// <c>coverage.yml</c> and the pull request to them before anything reaches the writer (<c>MergeQueueYml</c>, the saga's
/// enqueue checks).
/// </summary>
/// <remarks>
/// Two copies drifted once: the parser accepted an empty <c>regeneratedPaths</c>, more than 64 globs or a branch name the
/// writer narrows away, the writer answered every such request with a bare 400, and the saga retried that as a transient
/// failure six times before evicting with a generic error. Whatever the parser lets through, the writer now accepts.
/// </remarks>
public static partial class MergeQueueWireRules
{
    /// <summary>The longest commit message the writer writes.</summary>
    public const int MaxMessageLength = 64 * 1024;

    /// <summary>The most <c>regeneratedPaths</c> globs, <c>autoResolve</c> entries, and keys in one entry.</summary>
    public const int MaxGlobs = 64;

    public const int MaxGlobLength = 256;

    public const int MaxKeyLength = 256;

    public const int MaxBranchLength = 200;

    /// <summary>The longest identity name or email.</summary>
    public const int MaxIdentityLength = 256;

    /// <summary>The conflict strategies (§10.3): <c>take-base</c> keeps the base branch's file (the regenerate workflow
    /// then rewrites it); <c>evict</c> refuses the conflict, and in <c>coverage.yml</c> masks a default for one path.</summary>
    public const string TakeBase = "take-base", Evict = "evict";

    /// <summary><c>keep-both</c> (O33, §10.4): both sides' insertions at one place, base branch first.</summary>
    public const string KeepBoth = "keep-both";

    /// <summary>Every strategy a rule may name; the writer's <c>ConflictResolverRegistry</c> holds exactly these.</summary>
    public static IReadOnlySet<string> Strategies { get; } = new HashSet<string>(StringComparer.Ordinal) { TakeBase, KeepBoth, Evict };

    /// <summary>The content validators (§10.4 step 4). <c>no-conflict-markers</c> runs on every resolved content anyway;
    /// naming it is allowed and changes nothing.</summary>
    public const string Utf8 = "utf8", Json = "json", JsonNoDuplicateKeys = "json-no-duplicate-keys", Xml = "xml", NoConflictMarkers = "no-conflict-markers";

    /// <summary>Every validator a rule may name; the writer's <c>ConflictResolverRegistry</c> holds exactly these.</summary>
    public static IReadOnlySet<string> Validators { get; } = new HashSet<string>(StringComparer.Ordinal) { Utf8, Json, JsonNoDuplicateKeys, Xml, NoConflictMarkers };

    /// <summary>The most validators one rule names.</summary>
    public const int MaxValidators = 8;

    public const string BranchRule =
        "letters, digits, '.', '_', '/' and '-' only, 1 to 200 characters, not starting with '-', '/' or '.', not ending "
        + "with '/' or '.', no '..' or '//', and no part starting with '.' or ending in '.lock'";

    public const string GlobRule = "1 to 256 characters, no control character or backslash, not starting with '/'";

    public const string KeyRule = "1 to 256 characters, no control character";

    public const string MessageRule = "1 to 65536 characters, not blank, no NUL";

    public const string IdentityRule = "a name and an email of 1 to 256 characters without '<', '>' or a line break, and no space in the email";

    [GeneratedRegex(@"\A[A-Za-z0-9._/-]{1,200}\z", RegexOptions.CultureInvariant)]
    private static partial Regex BranchChars();

    /// <summary>A branch name without <c>refs/heads/</c>, as <c>git check-ref-format</c> allows it, narrowed to a
    /// conservative character set so no value can be read as an option, a refspec or a revision expression.</summary>
    public static bool IsBranch(string? value)
    {
        if (value is null || !BranchChars().IsMatch(value) || value[0] is '-' or '/' or '.' || value[^1] is '/' or '.'
            || value.Contains("..", StringComparison.Ordinal) || value.Contains("//", StringComparison.Ordinal) || value.Contains("@{", StringComparison.Ordinal))
            return false;
        foreach (var component in value.Split('/'))
        {
            if (component.StartsWith('.') || component.EndsWith(".lock", StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    /// <summary>The queue branch of pull request <paramref name="number"/> into <paramref name="baseBranch"/> (D5).
    /// <c>MergeQueueEntry.QueueBranch</c> composes the same name.</summary>
    public static string QueueBranchName(string baseBranch, int number)
        => $"mq/{baseBranch}/pr-{number.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>A valid branch under <c>mq/</c>: the only names the writer creates, resets or deletes.</summary>
    public static bool IsQueueBranch(string? value)
        => IsBranch(value) && value!.StartsWith("mq/", StringComparison.Ordinal) && value.Length > 3;

    /// <summary>A base branch whose queue branches are valid too, for any pull request number.</summary>
    public static bool IsQueueableBase(string? value)
        => IsBranch(value) && IsQueueBranch(QueueBranchName(value!, int.MaxValue));

    /// <summary>A repository-relative path glob (<c>regeneratedPaths</c>, <c>autoResolve[].path</c>).</summary>
    public static bool IsGlob(string? value)
        => value is { Length: > 0 and <= MaxGlobLength } && !value.Any(c => char.IsControl(c) || c == '\\') && !value.StartsWith('/')
           && !string.IsNullOrWhiteSpace(value);

    /// <summary>A top-level JSON member name of a key-scoped rule (O2).</summary>
    public static bool IsKey(string? value) => value is { Length: > 0 and <= MaxKeyLength } && !value.Any(char.IsControl);

    public static bool IsMessage(string? value)
        => value is { Length: > 0 and <= MaxMessageLength } && !value.Contains('\0') && !string.IsNullOrWhiteSpace(value);

    /// <summary>A name git accepts in an identity (<see cref="GitIdentity"/>), without a length limit.</summary>
    public static bool IsIdentityName(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.IndexOfAny(['<', '>', '\n', '\r', '\0']) < 0;

    /// <summary>An email git accepts in an identity (<see cref="GitIdentity"/>), without a length limit.</summary>
    public static bool IsIdentityEmail(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.IndexOfAny(['<', '>', '\n', '\r', '\0', ' ']) < 0;

    /// <summary>An identity the writer accepts: an author, a committer, or a <c>Co-authored-by:</c> trailer (§0 D60).</summary>
    public static bool IsIdentity(string? name, string? email)
        => name is { Length: <= MaxIdentityLength } && email is { Length: <= MaxIdentityLength } && IsIdentityName(name) && IsIdentityEmail(email);
}
