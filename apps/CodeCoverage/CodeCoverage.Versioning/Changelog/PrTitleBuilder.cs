using System.Text.RegularExpressions;

namespace CodeCoverage.Versioning.Changelog;

/// <summary>
/// Builds the corrected title of a pull request, <c>&lt;type&gt;: &lt;title&gt; (#issue/#issue)</c>
/// (docs/coverage_package_versioning_PRD.md D14). Pure: the issue labels come in through a delegate.
/// </summary>
/// <remarks>
/// <para>
/// <b>The type</b> is the first of: the type a person chose with the check's button; the ticked box of the PR template; the
/// type already in the title; a <c>bug</c> label on a linked issue (<c>fix</c>). A bot's pull request keeps the type in its
/// title (<c>chore(deps)</c>) before anything else is read. Nothing else counts (#693 D6, D12): the branch name is never
/// read, because people name branches freely, and there is no <c>feat</c> default. Without a signal the title stays
/// untyped and the check asks.
/// </para>
/// <para>
/// <b>Linked issues</b> are the ones the body closes with GitHub's keywords (<c>Closes #12</c>, <c>fixes owner/repo#12</c>),
/// of this repository only. A link made in the sidebar alone is not seen: it is not in the payload, and reading it needs
/// GraphQL for one suffix.
/// </para>
/// <para>
/// <b>A fixed point:</b> building from a corrected title gives the same title, so the app's own edit, which comes back as
/// an <c>edited</c> event, changes nothing. That is the loop guard; skipping bot senders is only a shortcut.
/// </para>
/// </remarks>
public static partial class PrTitleBuilder
{
    /// <summary>The conventional-commit types the builder recognises, in a title and in the template.</summary>
    public static readonly IReadOnlySet<string> Types = new HashSet<string>(StringComparer.Ordinal)
    {
        "feat", "fix", "chore", "docs", "refactor", "perf", "test", "build", "ci", "style", "revert",
    };

    /// <summary>Template wording that maps to a type, for boxes like "- [x] Bug fix" or "- [x] New feature".</summary>
    private static readonly (string Word, string Type)[] TemplateWords =
    [
        ("bug", "fix"), ("fix", "fix"), ("feature", "feat"), ("feat", "feat"), ("documentation", "docs"), ("docs", "docs"),
        ("refactor", "refactor"), ("performance", "perf"), ("perf", "perf"), ("test", "test"), ("chore", "chore"),
        ("build", "build"), ("ci", "ci"), ("style", "style"), ("revert", "revert"),
    ];

    /// <summary>Builds the title. Null when there is nothing to build from (an empty title).</summary>
    /// <param name="repositoryFullName"><c>owner/name</c>, to keep cross-repository references out.</param>
    /// <param name="issueLabels">The labels of an issue of this repository, or null when the number is no issue.</param>
    /// <param name="chosenType">The type a person chose with the check's button (#693 D12); null otherwise.</param>
    public static async Task<PrTitleResult?> BuildAsync(
        string title, string? body, bool authorIsBot, string repositoryFullName,
        Func<int, CancellationToken, Task<IReadOnlyList<string>?>> issueLabels, string? chosenType = null, CancellationToken cancellationToken = default)
    {
        var parsed = Parse(title);
        if (parsed.Text.Length == 0) return null;

        var linked = LinkedIssues(body, repositoryFullName);

        var labels = new Dictionary<int, IReadOnlyList<string>?>();
        async Task<IReadOnlyList<string>?> LabelsOf(int number)
        {
            if (!labels.TryGetValue(number, out var found))
                labels[number] = found = await issueLabels(number, cancellationToken);
            return found;
        }

        string? type;
        EPrTitleTypeSource source;
        if (authorIsBot && parsed.Type is not null)
            (type, source) = (parsed.Type, EPrTitleTypeSource.Title);
        else if (chosenType is not null && Types.Contains(chosenType))
            (type, source) = (chosenType, EPrTitleTypeSource.Button);
        else if (TemplateType(body) is { } ticked)
            (type, source) = (ticked, EPrTitleTypeSource.Template);
        else if (parsed.Type is not null)
            (type, source) = (parsed.Type, EPrTitleTypeSource.Title);
        else if (await AnyBugAsync(linked, LabelsOf))
            (type, source) = ("fix", EPrTitleTypeSource.LinkedIssue);
        else
            (type, source) = (null, EPrTitleTypeSource.None);

        // A scope and a breaking-change bang belong to the type the title had; a type from elsewhere starts bare.
        var prefix = type is null ? "" : $"{type}{(type == parsed.Type ? parsed.Scope + parsed.Bang : "")}: ";
        var suffix = linked.Count == 0 ? "" : $" ({string.Join('/', linked.Select(i => $"#{i}"))})";
        return new PrTitleResult($"{prefix}{parsed.Text}{suffix}", type, source, linked);
    }

    private static async Task<bool> AnyBugAsync(IEnumerable<int> issues, Func<int, Task<IReadOnlyList<string>?>> labelsOf)
    {
        foreach (var issue in issues)
            if ((await labelsOf(issue))?.Any(l => string.Equals(l, "bug", StringComparison.OrdinalIgnoreCase)) == true)
                return true;
        return false;
    }

    /// <summary>The title split into a recognised type, its scope and bang, and the text without any issue suffixes.</summary>
    internal static (string? Type, string Scope, string Bang, string Text) Parse(string title)
    {
        var text = (title ?? "").Trim();
        string? type = null, scope = "", bang = "";
        if (TypePrefix().Match(text) is { Success: true } m && Types.Contains(m.Groups["type"].Value.ToLowerInvariant()))
        {
            type = m.Groups["type"].Value.ToLowerInvariant();
            scope = m.Groups["scope"].Value;
            bang = m.Groups["bang"].Value;
            text = text[m.Length..].Trim();
        }

        // Every trailing "(#1)" or "(#1/#2)" goes: the builder writes its own, and a stale one would stack.
        while (IssueSuffix().Match(text) is { Success: true } suffix)
            text = text[..suffix.Index].TrimEnd();
        return (type, scope, bang, text);
    }

    /// <summary>The issues the body closes, of this repository, in order of appearance, without duplicates.</summary>
    internal static IReadOnlyList<int> LinkedIssues(string? body, string repositoryFullName)
    {
        if (string.IsNullOrEmpty(body)) return [];
        var found = new List<int>();
        foreach (Match m in ClosingReference().Matches(body))
        {
            var repo = m.Groups["repo"].Value;
            if (repo.Length > 0 && !string.Equals(repo, repositoryFullName, StringComparison.OrdinalIgnoreCase)) continue;
            if (int.TryParse(m.Groups["n"].Value, out var n) && n > 0 && !found.Contains(n)) found.Add(n);
        }
        return found;
    }

    /// <summary>The type of the first ticked template box whose text starts with a known word.</summary>
    internal static string? TemplateType(string? body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        foreach (Match m in TickedBox().Matches(body))
        {
            var text = m.Groups["text"].Value.TrimStart('*', '_', '`', ' ').ToLowerInvariant();
            foreach (var (word, type) in TemplateWords)
                if (text.StartsWith(word, StringComparison.Ordinal) && (text.Length == word.Length || !char.IsLetter(text[word.Length])))
                    return type;
        }
        return null;
    }

    [GeneratedRegex(@"^(?<type>[A-Za-z]+)(?<scope>\([^)\r\n]*\))?(?<bang>!)?\s*:\s*")]
    private static partial Regex TypePrefix();

    [GeneratedRegex(@"\s*\(#\d+(?:\s*/\s*#\d+)*\)$")]
    private static partial Regex IssueSuffix();

    [GeneratedRegex(@"(?i)\b(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)\s*:?\s+(?<repo>[A-Za-z0-9-]+/[A-Za-z0-9._-]+)?#(?<n>\d{1,9})\b")]
    private static partial Regex ClosingReference();

    [GeneratedRegex(@"(?im)^\s*[-*]\s*\[[xX]\]\s*(?<text>.+)$")]
    private static partial Regex TickedBox();
}

/// <summary>The built title and how its type was decided, for the check run; <see cref="Type"/> is null when nothing gave one.</summary>
public sealed record PrTitleResult(string Title, string? Type, EPrTitleTypeSource TypeSource, IReadOnlyList<int> Issues);

/// <summary>Where the type came from (D14's order, as #693 D6 and D12 changed it).</summary>
public enum EPrTitleTypeSource
{
    Button,
    Template,
    Title,
    LinkedIssue,
    /// <summary>No explicit signal: the title stays untyped and the check offers the buttons.</summary>
    None,
}
