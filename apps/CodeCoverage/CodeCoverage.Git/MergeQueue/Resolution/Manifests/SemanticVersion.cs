using System.Globalization;
using System.Text.RegularExpressions;

namespace CodeCoverage.Git.MergeQueue.Resolution.Manifests;

/// <summary>A NuGet/SemVer 2 version: 2 to 4 numeric parts (padded to 4), an optional prerelease, build metadata ignored.
/// Floating versions, ranges and variables do not parse. Numeric prerelease identifiers compare as numbers and rank below
/// alphanumeric ones; a release outranks its prereleases.</summary>
internal sealed partial record SemanticVersion(long[] Numbers, string[] Prerelease) : IComparable<SemanticVersion>
{
    public static SemanticVersion? Parse(string text)
    {
        if (Pattern().Match(text.Trim()) is not { Success: true } m)
            return null;
        var numbers = m.Groups["numbers"].Value.Split('.').Select(n => long.Parse(n, CultureInfo.InvariantCulture)).ToList();
        while (numbers.Count < 4)
            numbers.Add(0);
        return new SemanticVersion([.. numbers], m.Groups["pre"].Success ? m.Groups["pre"].Value.Split('.') : []);
    }

    /// <summary>The order of two version texts, or null when either does not parse.</summary>
    public static int? Compare(string a, string b)
        => Parse(a) is { } va && Parse(b) is { } vb ? va.CompareTo(vb) : null;

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
            return 1;
        for (var i = 0; i < 4; i++)
        {
            var c = Numbers[i].CompareTo(other.Numbers[i]);
            if (c != 0)
                return c;
        }
        if (Prerelease.Length == 0 || other.Prerelease.Length == 0)
            return Prerelease.Length == other.Prerelease.Length ? 0 : Prerelease.Length == 0 ? 1 : -1;
        for (var i = 0; i < Math.Min(Prerelease.Length, other.Prerelease.Length); i++)
        {
            var (a, b) = (Prerelease[i], other.Prerelease[i]);
            var numericA = long.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out var na);
            var numericB = long.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out var nb);
            var c = numericA && numericB ? na.CompareTo(nb) : numericA ? -1 : numericB ? 1 : string.CompareOrdinal(a.ToLowerInvariant(), b.ToLowerInvariant());
            if (c != 0)
                return c;
        }
        return Prerelease.Length.CompareTo(other.Prerelease.Length);
    }

    [GeneratedRegex(@"^(?<numbers>\d{1,9}\.\d{1,9}(\.\d{1,9}){0,2})(-(?<pre>[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*))?(\+[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
