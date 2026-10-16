using CodeCoverage.Git.Dependencies;

namespace CodeCoverage.Dependencies.Policy;

/// <summary>The size of an update, as the policy reads it. The values line up with <c>EDependencyThreshold</c>
/// (<c>Never = 0</c>), so <c>(int)level &lt;= (int)threshold</c> is "within the threshold".</summary>
public enum EUpdateLevel
{
    Patch = 1,
    Minor = 2,
    Major = 3,
}

/// <summary>
/// R17 guard 1: <b>a version floor that can only raise the update type.</b> If the leading numeric segment differs (or the
/// second one, for <c>0.x</c>), the update counts as major whatever the label says.
/// </summary>
/// <remarks>
/// <para>
/// String comparison, not version parsing (R1 holds): the floor reads the first run of digits after an optional operator
/// or <c>v</c> prefix, and for a <c>0</c> the run after the next dot. Nothing else of a version is ever read.
/// </para>
/// <para>
/// A token with no leading number (a codename such as ubuntu <c>resolute</c>, a branch) counts as major: the floor cannot
/// prove it is not one. CalVer and date tags differ in their leading segment, so they classify as major too; both need a
/// rule before they can auto-merge (R17's stated cost).
/// </para>
/// </remarks>
public static class UpdateTypeFloor
{
    /// <summary>The level Renovate's label claims, or null when the label cannot be planned without a human
    /// (<c>rollback</c>, <c>replacement</c>: gap D #17 "a replacement without a rule").</summary>
    public static EUpdateLevel? Labeled(EUpdateType? label) => label switch
    {
        EUpdateType.Major => EUpdateLevel.Major,
        EUpdateType.Minor => EUpdateLevel.Minor,
        // bump rewrites a range's floor; the floor below still catches a major.
        EUpdateType.Bump => EUpdateLevel.Minor,
        EUpdateType.Patch or EUpdateType.Digest or EUpdateType.Pin or EUpdateType.PinDigest or EUpdateType.LockfileUpdate => EUpdateLevel.Patch,
        _ => null,
    };

    /// <summary>The level after the floor: <paramref name="labeled"/>, raised to major when the leading segments of
    /// <paramref name="current"/> and <paramref name="next"/> differ (or their second segments, under <c>0.x</c>).</summary>
    public static EUpdateLevel Apply(EUpdateLevel labeled, string? current, string? next)
    {
        if (labeled == EUpdateLevel.Major)
            return EUpdateLevel.Major;
        if (current is null || next is null)
            return EUpdateLevel.Major;
        if (string.Equals(current, next, StringComparison.Ordinal))
            return labeled;

        var a = Leading(current);
        var b = Leading(next);
        if (a is null || b is null || a.Value.Major != b.Value.Major)
            return EUpdateLevel.Major;
        if (a.Value.Major == "0" && a.Value.Second != b.Value.Second)
            return EUpdateLevel.Major;
        return labeled;
    }

    /// <summary>The leading numeric segment of a token (and the one after it), leading zeros dropped; null when the token
    /// does not start with a number after an operator or <c>v</c> prefix.</summary>
    public static (string Major, string? Second)? Leading(string token)
    {
        var i = 0;
        // An operator prefix: ^ ~ = > < ! and spaces, then at most one v/V.
        while (i < token.Length && token[i] is '^' or '~' or '=' or '>' or '<' or '!' or ' ')
            i++;
        if (i < token.Length && token[i] is 'v' or 'V')
            i++;
        var start = i;
        while (i < token.Length && char.IsAsciiDigit(token[i]))
            i++;
        if (i == start)
            return null;
        var major = Trim(token[start..i]);

        string? second = null;
        if (i + 1 < token.Length && token[i] == '.' && char.IsAsciiDigit(token[i + 1]))
        {
            var j = i + 1;
            while (j < token.Length && char.IsAsciiDigit(token[j]))
                j++;
            second = Trim(token[(i + 1)..j]);
        }
        return (major, second);

        static string Trim(string digits) => digits.TrimStart('0') is { Length: > 0 } t ? t : "0";
    }

    /// <summary>The leading segment as a number, for ordering candidates of one record; null when there is none or it is
    /// absurdly long.</summary>
    public static long? LeadingNumber(string? token)
        => token is not null && Leading(token) is { } leading && leading.Major.Length <= 15 ? long.Parse(leading.Major, System.Globalization.CultureInfo.InvariantCulture) : null;

    /// <summary>The leading segment an approval is bound to (gap E: "target major"); the token itself when it has none.</summary>
    public static string TargetMajor(string token) => Leading(token) is { } leading
        ? leading.Major == "0" && leading.Second is { } second ? $"0.{second}" : leading.Major
        : token;
}
