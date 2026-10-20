using System.Globalization;
using System.Text;

namespace MintPlayer.Spark.Gs1;

/// <summary>
/// Spoken check digits for warehouse locations (docs/warehouse_picking_PRD.md §4.5 Locations, §4.7 Scan-to-confirm): a
/// short number printed on the location label, which a picker keys in instead of scanning to prove they are at the
/// location. They are not a GS1 check digit, which anyone can compute from the code; a location identified by a GLN
/// (AI 414) carries its GS1 check digit through <see cref="Gs1CheckDigit"/> as well.
/// </summary>
/// <remarks>
/// The digits are derived, never stored by hand: the 32-bit FNV-1a hash of the UTF-8 bytes of
/// <c>secret + U+001F + code</c>, modulo 10^<c>length</c>, zero-padded. A per-site secret keeps them from being
/// worked out from the code on the label. The algorithm is deliberately simple so that ng-spark's <c>scanning</c>
/// entry point can run the same derivation; the browser only validates when the server sends it the expected digits.
/// Changing the secret changes every label, so it is configuration, not a credential to rotate.
/// </remarks>
public static class LocationCheckDigits
{
    /// <summary>The default number of digits: two, as on most WMS location labels.</summary>
    public const int DefaultLength = 2;

    /// <summary>The check digits of <paramref name="locationCode"/>.</summary>
    /// <param name="locationCode">The location's human code, as printed on its label. Compared exactly (case and spaces matter).</param>
    /// <param name="secret">The site's secret, or empty.</param>
    /// <param name="length">The number of digits, 1–6.</param>
    public static string Compute(string locationCode, string? secret = null, int length = DefaultLength)
    {
        ArgumentException.ThrowIfNullOrEmpty(locationCode);
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, 6);

        var hash = Fnv1a32(Encoding.UTF8.GetBytes((secret ?? string.Empty) + '\u001F' + locationCode));
        var modulus = (uint)Math.Pow(10, length);
        return (hash % modulus).ToString(CultureInfo.InvariantCulture).PadLeft(length, '0');
    }

    /// <summary>True when <paramref name="keyed"/> are the check digits of <paramref name="locationCode"/>.</summary>
    public static bool IsValid(string locationCode, string? keyed, string? secret = null, int length = DefaultLength)
        => keyed is not null && keyed.Length == length && string.Equals(Compute(locationCode, secret, length), keyed, StringComparison.Ordinal);

    private static uint Fnv1a32(ReadOnlySpan<byte> bytes)
    {
        var hash = 2166136261u;
        foreach (var b in bytes)
        {
            hash ^= b;
            hash = unchecked(hash * 16777619u);
        }
        return hash;
    }
}
