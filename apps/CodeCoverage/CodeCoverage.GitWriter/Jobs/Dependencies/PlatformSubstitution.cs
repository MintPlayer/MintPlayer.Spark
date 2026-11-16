using System.Text.RegularExpressions;
using CodeCoverage.Git.Dependencies;

namespace CodeCoverage.GitWriter.Jobs.Dependencies;

/// <summary>One location the lookup reported for an SDK (platform-record.schema.json): the file, the kind and the value as
/// written today. Only non-computed locations are sent (R8: a computed value is reported, never edited).</summary>
public sealed record DependencyPlatformLocation(string Sdk, string File, string Kind, string Value);

/// <summary>A platform group of the plan (<c>platform:dotnet 11.0</c>), its move, and the locations the lookup reported for
/// its SDK: what the SDK adapter of the apply run may edit (#613 item 2).</summary>
public sealed record DependencyPlatformMove(string Group, PlanPlatform Move, IReadOnlyList<DependencyPlatformLocation> Locations);

/// <summary>
/// #613 item 2: the line rule of a platform move. A changed line is admitted only as a substitution of one reported
/// location's current value by a new value token, everything else on the line byte-identical. The one exception is the
/// MSBuild element rename of <c>strategy: add</c> on a single-target project (<c>&lt;TargetFramework&gt;</c> becomes
/// <c>&lt;TargetFrameworks&gt;</c>, applyPlatform.ts <c>replacementFor</c>, kind <c>tfm</c>). The new token:
/// <list type="bullet">
/// <item>is one token of version characters (<see cref="Token"/>): no quote, angle bracket, space, <c>$</c>, <c>#</c>,
/// <c>:</c>, comma or bracket, so it can add no XML, YAML, JSON or shell structure to the line;</item>
/// <item>names the move's target: it holds its <c>toChannel</c>, <c>toVersion</c> or <c>sdkVersion</c>.</item>
/// </list>
/// Which file a location is in comes from the lookup; the file at <c>expected_sha</c> must still hold the value on the
/// changed line, so the lookup can only narrow what is admitted.
/// </summary>
public static partial class PlatformSubstitution
{
    /// <summary>One value token: an optional range operator (<c>engines</c>), then version characters; <c>;</c> separates
    /// TFMs (<c>net11.0;net10.0</c>).</summary>
    [GeneratedRegex(@"\A(?:>=|\^|~)?[0-9A-Za-z][0-9A-Za-z.+*_;-]{0,127}\z", RegexOptions.CultureInvariant)]
    public static partial Regex Token();

    private const string Single = "TargetFramework>";
    private const string Plural = "TargetFrameworks>";

    /// <summary>The platform group whose reported location explains <paramref name="removed"/> → <paramref name="added"/>,
    /// or null when none does.</summary>
    public static string? Explain(string removed, string added, IEnumerable<(DependencyPlatformMove Move, DependencyPlatformLocation Location)> candidates)
    {
        if (removed == added)
            return null;
        foreach (var (move, location) in candidates)
        {
            var value = location.Value;
            if (value.Length == 0)
                continue;
            if (location.Kind == TfmProperty)
            {
                if (PropertyMove(removed, added, value, move.Move))
                    return move.Group;
                continue;
            }
            if (location.Kind == ActionRuntime)
            {
                if (RuntimeMove(removed, added, value, move.Move))
                    return move.Group;
                continue;
            }
            for (var at = removed.IndexOf(value, StringComparison.Ordinal); at >= 0; at = removed.IndexOf(value, at + 1, StringComparison.Ordinal))
            {
                var (prefix, suffix) = (removed[..at], removed[(at + value.Length)..]);
                if (Fits(prefix, suffix, added, value, move.Move))
                    return move.Group;
                // strategy: add on a single-target project renames the element on both sides of the value.
                if (location.Kind == "tfm" && prefix.EndsWith("<" + Single, StringComparison.Ordinal) && suffix.StartsWith("</" + Single, StringComparison.Ordinal)
                    && Fits(prefix[..^Single.Length] + Plural, "</" + Plural + suffix[(2 + Single.Length)..], added, value, move.Move))
                    return move.Group;
            }
            if (EveryOccurrence(removed, added, value, move.Move))
                return move.Group;
        }
        return null;
    }

    /// <summary>
    /// platform-locations-PRD M5: a line holding the reported value more than once (a template.json choice and its
    /// description, <c>{ "choice": "net10.0", "description": "Target net10.0" }</c>) changes as every occurrence replaced by the
    /// same new token, the text between them byte-identical.
    /// </summary>
    private static bool EveryOccurrence(string removed, string added, string value, PlanPlatform move)
    {
        var parts = removed.Split(value);
        if (parts.Length < 3)
            return false;
        var rest = added.Length - parts.Sum(p => p.Length);
        if (rest <= 0 || rest % (parts.Length - 1) != 0)
            return false;
        var token = added.Substring(parts[0].Length, rest / (parts.Length - 1));
        return token != value && Token().IsMatch(token) && NamesTarget(token, move) && string.Join(token, parts) == added;
    }

    private static bool Fits(string prefix, string suffix, string added, string value, PlanPlatform move)
    {
        if (added.Length < prefix.Length + suffix.Length || !added.StartsWith(prefix, StringComparison.Ordinal) || !added.EndsWith(suffix, StringComparison.Ordinal))
            return false;
        var token = added[prefix.Length..^suffix.Length];
        return token != value && Token().IsMatch(token) && NamesTarget(token, move);
    }

    /// <summary>platform-locations-PRD R4/P9: the defining token of a computed TFM in a <c>*.props</c>/<c>*.targets</c>.</summary>
    public const string TfmProperty = "tfm-property";

    /// <summary>A whole-line property definition with no attributes: <c>&lt;Name&gt;value&lt;/Name&gt;</c>.</summary>
    [GeneratedRegex(@"\A(?<lead>[ \t]*)<(?<name>[A-Za-z_][A-Za-z0-9_.-]{0,127})>(?<value>[^<]*)</\k<name>>(?<tail>[ \t]*)\z", RegexOptions.CultureInvariant)]
    private static partial Regex PropertyLine();

    /// <summary>
    /// P9: a <c>tfm-property</c> line changes only as <c>&lt;Name&gt;old&lt;/Name&gt;</c> → <c>&lt;Name&gt;new&lt;/Name&gt;</c>, the same
    /// name, indentation and nothing else on the line, where the whole value is the reported value and it is the move's
    /// from-major (<c>10</c>) or from-channel (<c>10.0</c> inside <c>net10.0</c>), and the new value is the same shape on the
    /// target. No attribute (a <c>Condition</c>), no second element, no expression can come or go.
    /// </summary>
    private static bool PropertyMove(string removed, string added, string value, PlanPlatform move)
    {
        if (PropertyLine().Match(removed) is not { Success: true } o || PropertyLine().Match(added) is not { Success: true } n
            || o.Groups["name"].Value != n.Groups["name"].Value || o.Groups["lead"].Value != n.Groups["lead"].Value || o.Groups["tail"].Value != n.Groups["tail"].Value)
            return false;
        var (from, to) = (o.Groups["value"].Value, n.Groups["value"].Value);
        var (fromMajor, toMajor) = (Major(move.FromChannel), Major(move.ToChannel));
        if (fromMajor is null || toMajor is null || move.FromChannel == move.ToChannel)
            return false;
        // A bare major: the whole value.
        if (value == fromMajor)
            return from == fromMajor && to == toMajor && move.FromChannel[fromMajor.Length..] == move.ToChannel[toMajor.Length..];
        // A channel, bare (`net$(X)` with X = 10.0) or as the whole TFM (`net10.0` -> `net11.0`); nothing else in the value.
        return value == move.FromChannel
               && (from == move.FromChannel && to == move.ToChannel || from == $"net{move.FromChannel}" && to == $"net{move.ToChannel}");
    }

    /// <summary>platform-locations-PRD P10: an action's <c>runs.using: nodeNN</c>.</summary>
    public const string ActionRuntime = "action-runtime";

    /// <summary>
    /// P10: <c>runs.using</c> changes as one occurrence of the reported runtime replaced by a runtime the plan lists
    /// (<see cref="PlanPlatform.ActionRuntimes"/>), the rest of the line byte-identical. The new runtime need not name the
    /// target (a move to Node 26 takes it to node24 while GitHub runs nothing newer), so the plan's list is the bound, not
    /// <see cref="NamesTarget"/>.
    /// </summary>
    private static bool RuntimeMove(string removed, string added, string value, PlanPlatform move)
    {
        if (move.ActionRuntimes is not { Count: > 0 } runtimes)
            return false;
        for (var at = removed.IndexOf(value, StringComparison.Ordinal); at >= 0; at = removed.IndexOf(value, at + 1, StringComparison.Ordinal))
        {
            var (prefix, suffix) = (removed[..at], removed[(at + value.Length)..]);
            if (added.Length <= prefix.Length + suffix.Length || !added.StartsWith(prefix, StringComparison.Ordinal) || !added.EndsWith(suffix, StringComparison.Ordinal))
                continue;
            var token = added[prefix.Length..^suffix.Length];
            if (token != value && runtimes.Contains(token, StringComparer.Ordinal))
                return true;
        }
        return false;
    }

    private static string? Major(string channel)
        => channel.IndexOf('.') is var dot and > 0 && channel[..dot].All(char.IsAsciiDigit) ? channel[..dot] : null;

    /// <summary>The token holds the move's target channel, version or SDK version.</summary>
    public static bool NamesTarget(string token, PlanPlatform move)
        => move.ToChannel.Length > 0 && token.Contains(move.ToChannel, StringComparison.Ordinal)
           || move.ToVersion is { Length: > 0 } version && token.Contains(version, StringComparison.Ordinal)
           || move.SdkVersion is { Length: > 0 } sdk && token.Contains(sdk, StringComparison.Ordinal);
}
