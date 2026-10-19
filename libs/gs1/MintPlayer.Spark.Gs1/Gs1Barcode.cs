namespace MintPlayer.Spark.Gs1;

/// <summary>One GS1 element: an Application Identifier (AI) and its value as encoded.</summary>
public sealed record Gs1Element(string Ai, string Value);

/// <summary>A parsed GS1 barcode or GS1 Digital Link URI.</summary>
public sealed class Gs1ScanResult
{
    internal Gs1ScanResult(string error, string errorAi, IReadOnlyList<Gs1Element> elements)
    {
        Error = error;
        ErrorAi = errorAi;
        Elements = elements;
    }

    /// <summary>Empty when the scan is valid GS1 data; otherwise a <c>GS1_*</c> code that names the failure.</summary>
    public string Error { get; }

    /// <summary>The AI that <see cref="Error"/> concerns, or empty.</summary>
    public string ErrorAi { get; }

    public bool IsValid => Error.Length == 0;

    /// <summary>The elements in scan order; for a Digital Link: the primary key, its qualifiers, then the data attributes.</summary>
    public IReadOnlyList<Gs1Element> Elements { get; }

    /// <summary>The value of the first element with <paramref name="ai"/>, or null.</summary>
    public string? Get(string ai)
    {
        foreach (var element in Elements)
        {
            if (element.Ai == ai) return element.Value;
        }
        return null;
    }

    /// <summary>The GTIN (AI 01), 14 digits.</summary>
    public string? Gtin => Get("01");

    /// <summary>The SSCC (AI 00), 18 digits.</summary>
    public string? Sscc => Get("00");

    /// <summary>The batch or lot number (AI 10).</summary>
    public string? Lot => Get("10");

    /// <summary>The serial number (AI 21).</summary>
    public string? Serial => Get("21");

    /// <summary>The expiry date (AI 17) as encoded, YYMMDD; a day of 00 means the last day of the month.</summary>
    public string? Expiry => Get("17");

    /// <summary>The count: AI 30 (variable count of items) or AI 37 (count of trade items in a logistic unit).</summary>
    public string? Count => Get("30") ?? Get("37");
}

/// <summary>
/// Parses GS1 barcodes and GS1 Digital Link URIs against GS1's Barcode Syntax Dictionary. The logic lives once, in
/// <c>pg/*.pg</c>, and ng-spark's <c>scanning</c> entry point runs the same code.
/// </summary>
public static class Gs1Barcode
{
    /// <summary>
    /// Parses any scanned GS1 payload: an element string (after a symbology identifier such as <c>]C1</c>, <c>]d2</c>,
    /// <c>]Q3</c>, <c>]e0</c> or <c>]E0</c>, or without one, GS-separated or bracketed) or a GS1 Digital Link URI.
    /// </summary>
    public static Gs1ScanResult Parse(string scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        return ToScanResult(Core.Gs1Core.gs1ParseScan(scan));
    }

    /// <summary>Parses GS1 element-string data only (see <see cref="Parse"/>).</summary>
    public static Gs1ScanResult ParseElementString(string data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return ToScanResult(Core.Gs1Core.gs1ParseElementString(data));
    }

    /// <summary>Parses a GS1 Digital Link URI only (see <see cref="Parse"/>).</summary>
    public static Gs1ScanResult ParseDigitalLink(string uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return ToScanResult(Core.Gs1Core.gs1ParseDigitalLink(uri));
    }

    private static Gs1ScanResult ToScanResult(Core.Gs1ParseResult result)
        => new(result.error, result.errorAi, result.elements.Select(e => new Gs1Element(e.ai, e.value)).ToArray());
}
