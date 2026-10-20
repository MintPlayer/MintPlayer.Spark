using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Gs1;
using MintPlayer.Spark.Warehouse.Models;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Warehouse.Barcodes;

/// <summary>The pipeline's verdict on a scan (PRD §4.6, Results).</summary>
public enum BarcodeResolutionState
{
    /// <summary>One match: proceed.</summary>
    Resolved,
    /// <summary>Several matches: the user chooses (the expected item, when among them, is pre-selected).</summary>
    Choose,
    /// <summary>No match: "unknown barcode", with the link-to-item action for users holding that right.</summary>
    Unknown,
}

/// <param name="Preselected">On a task: the expected item's match, when it is among <paramref name="Matches"/>.</param>
/// <param name="CanLinkToItem">Unknown only: whether the caller may register the code on an item.</param>
public sealed record BarcodeResolution(BarcodeResolutionState State, ScannedBarcode Scan, IReadOnlyList<BarcodeMatch> Matches,
    BarcodeMatch? Preselected = null, bool CanLinkToItem = false);

/// <summary>Runs the <see cref="IBarcodeResolutionStep"/>s in order and applies the owner filter and the pre-selection (PRD §4.6).</summary>
public interface IBarcodeResolver
{
    /// <param name="canLinkToItem">Whether the caller holds the link-to-item right (the host checks it; the pipeline only reports it).</param>
    Task<BarcodeResolution> ResolveAsync(BarcodeScanContext context, bool canLinkToItem = false, CancellationToken cancellationToken = default);
}

internal sealed partial class BarcodeResolver : IBarcodeResolver
{
    [Inject] private readonly IEnumerable<IBarcodeResolutionStep> steps;

    public async Task<BarcodeResolution> ResolveAsync(BarcodeScanContext context, bool canLinkToItem = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scan = new ScannedBarcode(BarcodeCodes.StripSymbologyIdentifier(context.Raw).Trim());
        foreach (var step in steps.OrderBy(s => s.Order))
        {
            var outcome = await step.ResolveAsync(context, scan, cancellationToken);
            scan = outcome.Scan;
            if (!outcome.IsResolved)
                continue;

            // WI5: another owner's items are never offered; site codes (no owner) always pass.
            var visible = outcome.Matches!.Where(m => m.OwnerId is null || context.VisibleOwnerIds.Contains(m.OwnerId)).ToList();
            if (visible.Count == 0)
                continue;
            var preselected = context.ExpectedItemId is null ? null : visible.FirstOrDefault(m => m.TargetId == context.ExpectedItemId);
            return visible.Count == 1
                ? new BarcodeResolution(BarcodeResolutionState.Resolved, scan, visible, visible[0])
                : new BarcodeResolution(BarcodeResolutionState.Choose, scan, visible, preselected);
        }
        return new BarcodeResolution(BarcodeResolutionState.Unknown, scan, [], CanLinkToItem: canLinkToItem);
    }
}

/// <summary>Code normalisation shared by the steps and the registry.</summary>
public static class BarcodeCodes
{
    /// <summary>Drops an AIM symbology identifier (<c>]C1</c>, <c>]d2</c>, <c>]Q3</c>, <c>]e0</c>, …) the scanner prefixed.</summary>
    public static string StripSymbologyIdentifier(string raw)
        => raw.Length >= 3 && raw[0] == ']' ? raw[3..] : raw;

    /// <summary>The registry form of a code: GTINs as 14 digits, anything else trimmed and upper-cased.</summary>
    public static string Normalize(string code)
        => Gs1Gtin.Normalize(code) ?? code.Trim().ToUpperInvariant();
}

/// <summary>Step 1 (100): GS1 element strings and Digital Link URIs enrich the scan; only an SSCC resolves it (to a logistic unit).</summary>
internal sealed class Gs1BarcodeStep : IBarcodeResolutionStep
{
    public int Order => 100;

    public ValueTask<BarcodeStepOutcome> ResolveAsync(BarcodeScanContext context, ScannedBarcode scan, CancellationToken cancellationToken = default)
    {
        var raw = context.Raw;
        var looksGs1 = raw.StartsWith("]C1", StringComparison.Ordinal) || raw.StartsWith("]d2", StringComparison.Ordinal)
            || raw.StartsWith("]Q3", StringComparison.Ordinal) || raw.StartsWith("]e0", StringComparison.Ordinal)
            || raw.Contains('\u001d') || raw.StartsWith("http", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("(", StringComparison.Ordinal);
        if (!looksGs1)
            return ValueTask.FromResult(BarcodeStepOutcome.Continue(scan));

        var parsed = Gs1Barcode.Parse(raw);
        if (!parsed.IsValid)
            return ValueTask.FromResult(BarcodeStepOutcome.Continue(scan));

        var enriched = scan with
        {
            Code = parsed.Gtin is { } gtin ? Gs1Gtin.Normalize(gtin) ?? gtin : scan.Code,
            Gtin = parsed.Gtin is { } g ? Gs1Gtin.Normalize(g) : null,
            Sscc = parsed.Sscc,
            LotCode = parsed.Lot,
            Expiry = ParseExpiry(parsed.Expiry),
            Serial = parsed.Serial,
            Count = decimal.TryParse(parsed.Count, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count) ? count : null,
            Elements = parsed.Elements.Select(e => new KeyValuePair<string, string>(e.Ai, e.Value)).ToList(),
        };
        if (enriched.Sscc is { } sscc && enriched.Gtin is null)
            return ValueTask.FromResult(BarcodeStepOutcome.Resolved(enriched, [new BarcodeMatch(BarcodeTargetKind.LogisticUnit, sscc, null, $"SSCC {sscc}")]));
        return ValueTask.FromResult(BarcodeStepOutcome.Continue(enriched));
    }

    /// <summary>AI 17 is YYMMDD; a day of 00 is the month's last day; the century per GS1 General Specifications 7.12 (−49/+50 years) is approximated as 20YY.</summary>
    internal static DateOnly? ParseExpiry(string? yymmdd)
    {
        if (yymmdd is not { Length: 6 } || !yymmdd.All(char.IsAsciiDigit))
            return null;
        var year = 2000 + int.Parse(yymmdd[..2], System.Globalization.CultureInfo.InvariantCulture);
        var month = int.Parse(yymmdd[2..4], System.Globalization.CultureInfo.InvariantCulture);
        var day = int.Parse(yymmdd[4..], System.Globalization.CultureInfo.InvariantCulture);
        if (month is < 1 or > 12)
            return null;
        var last = DateTime.DaysInMonth(year, month);
        if (day == 0)
            day = last;
        return day > last ? null : new DateOnly(year, month, day);
    }
}

/// <summary>Step 2 (200): the site's own codes: a location by its code at the scan's site (a point load, no index).</summary>
internal sealed partial class OwnCodesBarcodeStep : IBarcodeResolutionStep
{
    [Inject] private readonly IDocumentStore documentStore;

    public int Order => 200;

    public async ValueTask<BarcodeStepOutcome> ResolveAsync(BarcodeScanContext context, ScannedBarcode scan, CancellationToken cancellationToken = default)
    {
        if (context.SiteId is null || scan.IsGs1)
            return BarcodeStepOutcome.Continue(scan);

        string id;
        try
        {
            id = WarehouseLocation.IdFor(context.SiteId, scan.Code);
        }
        catch (ArgumentException)
        {
            return BarcodeStepOutcome.Continue(scan);
        }

        using var session = documentStore.OpenAsyncSession();
        var location = await session.LoadAsync<WarehouseLocation>(id, cancellationToken);
        return location is { Active: true }
            ? BarcodeStepOutcome.Resolved(scan, [new BarcodeMatch(BarcodeTargetKind.Location, location.Id!, null, location.Code)])
            : BarcodeStepOutcome.Continue(scan);
    }
}

/// <summary>
/// Step 3 (300): the registry, by id prefix <c>barcodes/{code}/</c> (no index). A case or pallet code counts its pack
/// quantity; several matches (vendor codes collide across owners and suppliers) are all returned.
/// </summary>
internal sealed partial class RegistryBarcodeStep : IBarcodeResolutionStep
{
    [Inject] private readonly IDocumentStore documentStore;

    public int Order => 300;

    public async ValueTask<BarcodeStepOutcome> ResolveAsync(BarcodeScanContext context, ScannedBarcode scan, CancellationToken cancellationToken = default)
    {
        var code = BarcodeCodes.Normalize(scan.Gtin ?? scan.Code);
        if (code.Length == 0 || code.Contains('/'))
            return BarcodeStepOutcome.Continue(scan);

        using var session = documentStore.OpenAsyncSession();
        var registrations = (await session.Advanced.LoadStartingWithAsync<BarcodeRegistration>(BarcodeRegistration.PrefixFor(code), pageSize: 64, token: cancellationToken)).ToList();
        if (registrations.Count == 0)
            return BarcodeStepOutcome.Continue(scan);

        var items = await session.LoadAsync<WarehouseItem>(registrations.Select(r => r.ItemId).Distinct(), cancellationToken);
        var matches = registrations
            .Where(r => items.GetValueOrDefault(r.ItemId) is { Active: true })
            .Select(r =>
            {
                var item = items[r.ItemId]!;
                return new BarcodeMatch(BarcodeTargetKind.Item, item.Id!, r.OrganizationId, $"{item.Sku} {item.Name}".Trim(), r.Level, r.Quantity, r.CodeType);
            })
            .ToList();
        return matches.Count == 0 ? BarcodeStepOutcome.Continue(scan) : BarcodeStepOutcome.Resolved(scan, matches);
    }
}
