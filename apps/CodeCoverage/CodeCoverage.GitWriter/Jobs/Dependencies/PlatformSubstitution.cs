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
        }
        return null;
    }

    private static bool Fits(string prefix, string suffix, string added, string value, PlanPlatform move)
    {
        if (added.Length < prefix.Length + suffix.Length || !added.StartsWith(prefix, StringComparison.Ordinal) || !added.EndsWith(suffix, StringComparison.Ordinal))
            return false;
        var token = added[prefix.Length..^suffix.Length];
        return token != value && Token().IsMatch(token) && NamesTarget(token, move);
    }

    /// <summary>The token holds the move's target channel, version or SDK version.</summary>
    public static bool NamesTarget(string token, PlanPlatform move)
        => move.ToChannel.Length > 0 && token.Contains(move.ToChannel, StringComparison.Ordinal)
           || move.ToVersion is { Length: > 0 } version && token.Contains(version, StringComparison.Ordinal)
           || move.SdkVersion is { Length: > 0 } sdk && token.Contains(sdk, StringComparison.Ordinal);
}
