using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeCoverage.Dependencies.Policy;

/// <summary>
/// A glob over a dependency name (PRD §13.2: rules match on <c>ecosystem</c>/<c>depName</c> globs): <c>*</c> matches any run
/// of characters, <c>/</c> included (<c>@angular/*</c>, <c>Microsoft.CodeAnalysis*</c>), <c>?</c> one character, and
/// matching is case-insensitive, because NuGet ids are.
/// </summary>
public static class DependencyGlob
{
    private static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.Ordinal);

    public static bool Matches(string glob, string name)
        => Cache.GetOrAdd(glob, Compile).IsMatch(name);

    /// <summary>How specific a glob is: its literal characters. The most specific matching glob wins; a glob with no
    /// wildcard beats every wildcard of the same length.</summary>
    public static int Specificity(string glob)
        => glob.Count(c => c is not ('*' or '?')) * 2 + (glob.Contains('*') || glob.Contains('?') ? 0 : 1);

    private static Regex Compile(string glob)
    {
        var pattern = new StringBuilder("^");
        foreach (var c in glob)
        {
            pattern.Append(c switch
            {
                '*' => ".*",
                '?' => ".",
                _ => Regex.Escape(c.ToString()),
            });
        }
        pattern.Append('$');
        return new Regex(pattern.ToString(), RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline,
            TimeSpan.FromMilliseconds(100));
    }
}
