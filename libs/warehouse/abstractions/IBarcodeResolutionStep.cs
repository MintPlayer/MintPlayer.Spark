namespace MintPlayer.Spark.Warehouse;

/// <summary>What a scan was for, so steps can narrow the result (PRD §4.6, Results: "a list narrowed by context").</summary>
/// <param name="Raw">The scan as received, symbology identifier and GS separators included.</param>
/// <param name="SiteId">The site the user is working in, or null.</param>
/// <param name="VisibleOwnerIds">The owners the user may see (WD3, WI5). Matches for any other owner are dropped.</param>
/// <param name="ExpectedItemId">On a task, the item the task expects; it is pre-selected when it is among the matches.</param>
/// <param name="TaskId">The <c>WarehouseTask</c> the scan belongs to, or null.</param>
public sealed record BarcodeScanContext(
    string Raw,
    string? SiteId,
    IReadOnlyCollection<string> VisibleOwnerIds,
    string? ExpectedItemId = null,
    string? TaskId = null);

/// <summary>
/// The scan as the pipeline has understood it so far. The GS1 step fills the GS1 fields from AI element strings or a
/// Digital Link (PRD §4.6, step 1); a scan that is not GS1 keeps only <see cref="Code"/>.
/// </summary>
/// <param name="Code">The code to look up: the raw scan without its symbology identifier, trimmed.</param>
/// <param name="Gtin">AI 01, normalised to 14 digits (§4.6, step 3), or null.</param>
/// <param name="Sscc">AI 00, 18 digits, or null.</param>
/// <param name="LotCode">AI 10, or null.</param>
/// <param name="Expiry">AI 17 as a date (a day of 00 is the month's last day), or null.</param>
/// <param name="Serial">AI 21, or null.</param>
/// <param name="Count">AI 30 or AI 37, or null.</param>
/// <param name="Elements">Every GS1 element in scan order, as (AI, value).</param>
public sealed record ScannedBarcode(
    string Code,
    string? Gtin = null,
    string? Sscc = null,
    string? LotCode = null,
    DateOnly? Expiry = null,
    string? Serial = null,
    decimal? Count = null,
    IReadOnlyList<KeyValuePair<string, string>>? Elements = null)
{
    /// <summary>True once the GS1 step has parsed the scan as GS1 data.</summary>
    public bool IsGs1 => Elements is { Count: > 0 };
}

/// <summary>What a barcode turned out to identify (PRD §4.6, step 2: own codes; step 3: items).</summary>
public enum BarcodeTargetKind
{
    Item,
    Location,
    /// <summary>A logistic unit, by its SSCC (AI 00).</summary>
    LogisticUnit,
    /// <summary>A tote or other container.</summary>
    Container,
    TaskLabel,
    ReturnLabel,
}

/// <summary>The registry's code types (PRD §4.6, Registry).</summary>
public enum BarcodeCodeType
{
    Gtin13,
    Gtin14,
    Upc,
    Supplier,
    Internal,
}

/// <summary>One thing a scan may mean.</summary>
/// <param name="TargetId">The document id of the item, location, container or label.</param>
/// <param name="OwnerId">The owner, for owner-bearing targets; null for a site's own codes (locations, totes).</param>
/// <param name="Label">A display text for the choice dialog: SKU and name, or the location code.</param>
/// <param name="Level">For an item: the packaging level the code is registered at.</param>
/// <param name="PackQuantity">For an item: the base units one scan counts (a case code counts its packs, §4.6).</param>
/// <param name="CodeType">For an item: the registry's code type, or null.</param>
public sealed record BarcodeMatch(
    BarcodeTargetKind Kind,
    string TargetId,
    string? OwnerId,
    string Label,
    PackagingLevel Level = PackagingLevel.Each,
    decimal PackQuantity = 1,
    BarcodeCodeType? CodeType = null);

/// <summary>
/// A step's answer: either it resolved the scan (<see cref="Matches"/> non-null, possibly several, never empty), or the
/// pipeline continues with <see cref="Scan"/>, which a step may have enriched (the GS1 step adds the parsed fields).
/// </summary>
public sealed record BarcodeStepOutcome(ScannedBarcode Scan, IReadOnlyList<BarcodeMatch>? Matches)
{
    /// <summary>The step has no answer; the next step runs with <paramref name="scan"/>.</summary>
    public static BarcodeStepOutcome Continue(ScannedBarcode scan) => new(scan, null);

    /// <summary>The step resolved the scan. Several matches mean the user chooses (§4.6, Results).</summary>
    public static BarcodeStepOutcome Resolved(ScannedBarcode scan, IReadOnlyList<BarcodeMatch> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        if (matches.Count == 0) throw new ArgumentException("A resolved scan has at least one match; continue instead.", nameof(matches));
        return new(scan, matches);
    }

    public bool IsResolved => Matches is not null;
}

/// <summary>
/// One step of the barcode resolution pipeline (PRD §4.6, WD8). Steps run in <see cref="Order"/> until one resolves
/// the scan. The built-ins are:
/// <list type="number">
/// <item>GS1 parse (100): AI element strings by symbology identifier or FNC1/GS structure, and Digital Link URIs. It
/// only enriches the scan, unless it finds an SSCC;</item>
/// <item>own codes (200): locations (with check digits), SSCCs, totes and containers, task and return labels;</item>
/// <item>item lookup (300) in the barcode registry, GTINs normalised to 14 digits.</item>
/// </list>
/// The engine then drops matches for owners outside <see cref="BarcodeScanContext.VisibleOwnerIds"/>, pre-selects
/// <see cref="BarcodeScanContext.ExpectedItemId"/>, and reports one match, a choice, or "unknown barcode". The same
/// pipeline runs in the browser for instant feedback; the server's run is authoritative.
/// </summary>
public interface IBarcodeResolutionStep
{
    /// <summary>The position in the pipeline; lower runs first.</summary>
    int Order { get; }

    ValueTask<BarcodeStepOutcome> ResolveAsync(BarcodeScanContext context, ScannedBarcode scan, CancellationToken cancellationToken = default);
}
