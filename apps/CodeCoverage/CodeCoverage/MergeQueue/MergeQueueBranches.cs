using CodeCoverage.Git.MergeQueue;
using CodeCoverage.Versioning.Graph;
using CodeCoverage.Versioning.Releases;

namespace CodeCoverage.MergeQueue;

/// <summary>
/// What <c>mergeQueue.branches</c> may hold (#570 M7, PRD D12): a branch name, a pattern whose <c>*</c> stands for one
/// path segment (<c>hotfix/*</c>), or <see cref="SupportedLines"/>, every branch <c>versioning.lines</c> names.
/// </summary>
/// <remarks>
/// A pattern starts with a fixed segment: a bare <c>*</c> would queue every top-level branch, which nobody means. <c>**</c>
/// and <c>?</c> are refused for the same reason, a bound the owner can read off the file. D4's lifecycle narrowing of
/// "supported" (keep/retire) is M8's; until then every branch the line pattern matches is a supported line.
/// </remarks>
public static class MergeQueueBranches
{
    public const string SupportedLines = "supported-lines";

    public static bool IsPattern(string entry) => entry == SupportedLines || entry.Contains('*');

    /// <summary>Whether a <c>*</c> entry is one the queue accepts; names and the keyword are checked elsewhere.</summary>
    public static bool IsValidPattern(string entry)
    {
        var star = entry.IndexOf('*');
        return star > 0 && entry.LastIndexOf('/', star) > 0 && !entry.Contains("**", StringComparison.Ordinal) && !entry.Contains('?')
            && MergeQueueWireRules.IsQueueableBase(entry.Replace('*', 'x'));
    }

    /// <summary>Whether <paramref name="branch"/> is one of <paramref name="entries"/>.</summary>
    public static bool Matches(IEnumerable<string> entries, string? linePattern, string branch)
        => entries.Any(entry => entry == SupportedLines ? IsLine(linePattern, branch)
            : entry.Contains('*') ? PatternMatches(entry, branch)
            : string.Equals(entry, branch, StringComparison.Ordinal));

    /// <summary>Segment by segment, so a <c>*</c> never spans a <c>/</c>.</summary>
    private static bool PatternMatches(string pattern, string branch)
    {
        var patterns = pattern.Split('/');
        var segments = branch.Split('/');
        if (patterns.Length != segments.Length) return false;
        for (var i = 0; i < patterns.Length; i++)
            if (!PathGlob.Segment(patterns[i], segments[i])) return false;
        return true;
    }

    /// <summary>Whether <paramref name="branch"/> is a release line of <c>versioning.lines</c>.</summary>
    public static bool IsLine(string? linePattern, string branch)
        => linePattern is not null && ReleaseLinePattern.TryParse(linePattern, out _) is { } pattern && pattern.TryMatch(branch, out _);
}
