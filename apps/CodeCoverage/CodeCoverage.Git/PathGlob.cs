using System.Text.RegularExpressions;
using CodeCoverage.Git.MergeQueue;

namespace CodeCoverage.Git;

/// <summary>A repository-relative path glob, with the same grammar as <see cref="AutoResolveRule.PathGlob"/>: <c>**</c>
/// spans directories, <c>*</c> and <c>?</c> stay within one, and matching is case-sensitive, like git paths. The writer
/// holds the regenerate patch to these (<c>regeneratedPaths</c>, T4).</summary>
public sealed class PathGlob
{
    private readonly Regex pattern;

    public PathGlob(string glob)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(glob);
        Glob = glob;
        pattern = new Regex(AutoResolveRule.GlobToRegex(glob), RegexOptions.CultureInvariant | RegexOptions.Singleline);
    }

    public string Glob { get; }

    public bool Matches(string path) => pattern.IsMatch(path);

    public override string ToString() => Glob;
}
