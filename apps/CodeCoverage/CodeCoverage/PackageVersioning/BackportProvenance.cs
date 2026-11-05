using System.Globalization;
using System.Text.RegularExpressions;
using CodeCoverage.Versioning.Releases;

namespace CodeCoverage.PackageVersioning;

/// <summary>
/// Which pull request a backport carries (#570 M8c, PRD §5.5, D7). The app's own backports hold a hidden
/// <c>&lt;!-- backport-of: owner/repo#N --&gt;</c> marker; a hand-made one is recognised by a "Backport of #N" body, a
/// <c>backport/&lt;N&gt;-&lt;line&gt;</c> head, or <c>git cherry-pick -x</c> trailers. Once it merges, the source pull request
/// gets a <c>backported: &lt;line&gt;</c> label, so "which lines have #N?" needs no git query.
/// </summary>
public static partial class BackportProvenance
{
    public const string BackportedPrefix = "backported: ";

    public static string Marker(string fullName, int number)
        => $"<!-- backport-of: {fullName}#{number.ToString(CultureInfo.InvariantCulture)} -->";

    /// <summary>The body with the marker after one blank line, however the body ends.</summary>
    public static string WithMarker(string body, string fullName, int number)
        => body.TrimEnd('\n') + "\n\n" + Marker(fullName, number);

    /// <summary>The source the app's marker names, only when it is in <paramref name="fullName"/>.</summary>
    public static int? FromMarker(string? body, string fullName)
    {
        if (body is null || MarkerPattern().Match(body) is not { Success: true } match) return null;
        return match.Groups["repo"].Value.Equals(fullName, StringComparison.OrdinalIgnoreCase) ? Number(match.Groups["n"].Value) : null;
    }

    /// <summary>A hand-made backport's source: a "Backport of #N" body first, else a <c>backport/N-…</c> head.</summary>
    public static int? FromText(string? body, string headRef)
    {
        if (body is not null && BodyPattern().Match(body) is { Success: true } match) return Number(match.Groups["n"].Value);
        return ParseHead(headRef)?.Number;
    }

    /// <summary>The commits <c>git cherry-pick -x</c> named, in order, without duplicates.</summary>
    public static IReadOnlyList<string> CherryPickedFrom(IEnumerable<string> messages)
        => [.. messages.SelectMany(m => TrailerPattern().Matches(m)).Select(m => m.Groups["sha"].Value.ToLowerInvariant()).Distinct(StringComparer.Ordinal)];

    /// <summary>The pull request and line a <c>backport/&lt;N&gt;-&lt;line&gt;</c> head names; null for any other branch.</summary>
    public static (int Number, string Line)? ParseHead(string headRef)
    {
        if (HeadPattern().Match(headRef) is not { Success: true } match || Number(match.Groups["n"].Value) is not { } number) return null;
        return (number, match.Groups["line"].Value);
    }

    /// <summary>The label for the source: the line's name when lines are on, else the branch's last segment (<c>8.x</c>).</summary>
    public static string BackportedLabel(string branch, ReleaseLinePattern? lines)
        => BackportedPrefix + (lines is not null && lines.TryMatch(branch, out var line) && line is not null ? line.ToString() : branch[(branch.LastIndexOf('/') + 1)..]);

    private static int? Number(string digits)
        => int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;

    [GeneratedRegex(@"<!-- backport-of: (?<repo>[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+)#(?<n>[0-9]{1,9}) -->", RegexOptions.CultureInvariant)]
    private static partial Regex MarkerPattern();

    [GeneratedRegex(@"\bbackport of #(?<n>[0-9]{1,9})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BodyPattern();

    [GeneratedRegex(@"\(cherry picked from commit (?<sha>[0-9a-fA-F]{40})\)", RegexOptions.CultureInvariant)]
    private static partial Regex TrailerPattern();

    [GeneratedRegex(@"\Abackport/(?<n>[0-9]{1,9})-(?<line>[A-Za-z0-9._-]+)\z", RegexOptions.CultureInvariant)]
    private static partial Regex HeadPattern();
}
