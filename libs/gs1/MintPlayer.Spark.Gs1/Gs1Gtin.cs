namespace MintPlayer.Spark.Gs1;

/// <summary>
/// Global Trade Item Numbers: GTIN-8, GTIN-12 (UPC-A), GTIN-13 (EAN-13) and GTIN-14. A barcode registry stores them
/// normalised to 14 digits, so the same item matches whichever length was scanned (docs/warehouse_picking_PRD.md §4.6).
/// </summary>
public static class Gs1Gtin
{
    /// <summary>True when <paramref name="gtin"/> is 8, 12, 13 or 14 ASCII digits with a valid check digit.</summary>
    public static bool IsValid(string? gtin) => HasGtinLength(gtin) && Gs1CheckDigit.IsValid(gtin);

    /// <summary>
    /// The GTIN-14 form of <paramref name="gtin"/>: left-padded with zeros, which leaves the check digit unchanged.
    /// Null when it is not a valid GTIN (see <see cref="IsValid"/>).
    /// </summary>
    public static string? Normalize(string? gtin) => IsValid(gtin) ? gtin!.PadLeft(14, '0') : null;

    private static bool HasGtinLength(string? gtin)
    {
        if (gtin is null || gtin.Length is not (8 or 12 or 13 or 14)) return false;
        foreach (var c in gtin)
        {
            if (c is < '0' or > '9') return false;
        }
        return true;
    }
}
