using System.Text.RegularExpressions;

namespace CodeCoverage.GitMirror.Graph;

/// <summary>
/// What a snapshot walks: one ref or all branches and tags, optionally first-parent only, optionally a message or author
/// search. Part of the snapshot key <c>(repoId, refsHash, filter)</c>.
/// Search values are bound as <c>--grep=&lt;v&gt;</c> / <c>--author=&lt;v&gt;</c> with <c>--fixed-strings</c> (D17, D21),
/// one array element each, so a value starting with <c>-</c> can never be read as an option and is never a regex.
/// </summary>
public sealed partial record GraphFilter(string? Ref, bool FirstParent, string? Grep, string? Author)
{
    public const int MaxSearchLength = 200;

    [GeneratedRegex(@"^refs/(heads|tags)/[A-Za-z0-9._/+@-]{1,240}$", RegexOptions.CultureInvariant)]
    private static partial Regex RefPattern();

    public static bool IsValidRef(string value) => RefPattern().IsMatch(value) && !value.Contains("..") && !value.Contains("@{");

    /// <summary>A search view lists matching commits; their parents are mostly not in the list, so they are laid out
    /// as a flat list (every row its own tip) instead of growing a dangling lane per result.</summary>
    public bool IsSearch => Grep is not null || Author is not null;

    public string CacheKey => $"ref={Ref}\nfp={FirstParent}\ngrep={Grep}\nauthor={Author}";

    /// <summary>The limiting options shared by <c>git log</c> and <c>git rev-list --count</c> (D21: same filter).</summary>
    public IEnumerable<string> LimitArguments()
    {
        if (FirstParent)
            yield return "--first-parent";
        if (IsSearch)
            yield return "--fixed-strings";
        if (Grep is not null)
        {
            yield return "--regexp-ignore-case";
            yield return "--grep=" + Grep;
        }
        if (Author is not null)
            yield return "--author=" + Author;
    }

    /// <summary>The revisions to walk; always last on the command line.</summary>
    public IEnumerable<string> RevisionArguments()
    {
        if (Ref is null)
        {
            yield return "--branches";
            yield return "--tags";
        }
        else
        {
            yield return "--end-of-options";
            yield return Ref;
        }
    }
}
