using MintPlayer.Spark.ReferenceData;

namespace MintPlayer.Spark.Warehouse.Models;

/// <summary>
/// A warehouse site (PRD §4.5, WD3): <c>warehouse-sites/{code}</c>. Cut-offs and day boundaries are wall-clock times
/// in <see cref="TimeZone"/> (§4.14, Q-W1); the zone comes from reference data, never converted here (K10).
/// </summary>
public sealed class WarehouseSite
{
    public const string IdPrefix = "warehouse-sites/";

    public static string IdFor(string code) => IdPrefix + WarehouseIds.Normalize(code);

    public string? Id { get; set; }

    /// <summary>The short code, unique; part of every location id of the site.</summary>
    public string Code { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>The site's address (reference-data <see cref="ReferenceData.Address"/>).</summary>
    public Address? Address { get; set; }

    /// <summary>The IANA zone (from reference data, D33), e.g. <c>Europe/Brussels</c>.</summary>
    public string TimeZone { get; set; } = "";

    public bool Active { get; set; } = true;
}

/// <summary>What a zone is for (PRD §4.5, <c>WarehouseZone</c>; WD5).</summary>
public enum WarehouseZoneKind
{
    Pick,
    Bulk,
    Cold,
    Quarantine,
    Returns,
    Staging,
    Other,
}

/// <summary>
/// What a zone accepts (PRD §4.5; round 5, commerce GC9: dangerous goods may need a segregated zone). Put-away and
/// replenishment ask <see cref="ZoneMixing.Allows"/> before they place stock.
/// </summary>
public sealed class ZoneMixingRules
{
    /// <summary>Whether dangerous goods (an item with <see cref="WarehouseItem.DangerousGoods"/>) may be stored here. Default true.</summary>
    public bool AllowDangerousGoods { get; set; } = true;

    /// <summary>When not empty: only dangerous goods of these hazard classes (e.g. <c>3</c>, <c>9</c>); others are refused.</summary>
    public List<string> AllowedHazardClasses { get; set; } = [];

    /// <summary>Only dangerous goods may be stored here (a segregated DG zone).</summary>
    public bool DangerousGoodsOnly { get; set; }
}

/// <summary>Whether a zone takes an item (GC9 segregation); put-away (WM4) and replenishment ask before placing stock.</summary>
public static class ZoneMixing
{
    public static bool Allows(WarehouseZone? zone, WarehouseItem item)
    {
        if (zone is null)
            return true;
        var rules = zone.Mixing;
        if (item.DangerousGoods is not { } dg)
            return !rules.DangerousGoodsOnly;
        if (!rules.AllowDangerousGoods)
            return false;
        return rules.AllowedHazardClasses.Count == 0
            || (dg.HazardClass is not null && rules.AllowedHazardClasses.Contains(dg.HazardClass, StringComparer.OrdinalIgnoreCase));
    }
}

/// <summary>A zone of a site (PRD §4.5): <c>{siteId}/zones/{code}</c>.</summary>
public sealed class WarehouseZone
{
    public static string IdFor(string siteId, string code) => $"{siteId}/zones/{WarehouseIds.Normalize(code)}";

    public string? Id { get; set; }

    public string SiteId { get; set; } = "";

    public string Code { get; set; } = "";

    public string Name { get; set; } = "";

    public WarehouseZoneKind Kind { get; set; }

    public ZoneMixingRules Mixing { get; set; } = new();
}

/// <summary>
/// A location (PRD §4.5, "Locations"): a separate document per location, <c>{siteId}/locations/{code}</c>, because a site
/// has 10⁴–10⁵ of them. Routing sorts by <see cref="WalkSequence"/>; the geometry is kept for a host's
/// <see cref="IPickRouter"/>. A location with ledger history is deactivated, never deleted.
/// </summary>
public sealed class WarehouseLocation
{
    public static string IdFor(string siteId, string code) => $"{siteId}/locations/{WarehouseIds.Normalize(code)}";

    public string? Id { get; set; }

    public string SiteId { get; set; } = "";

    public string? ZoneId { get; set; }

    /// <summary>The human code on the label, e.g. <c>A-01-03-B</c>.</summary>
    public string Code { get; set; } = "";

    /// <summary>The scannable code (defaults to <see cref="Code"/>).</summary>
    public string Barcode { get; set; } = "";

    /// <summary>Two check digits printed on the label and asked for when the code is keyed in by hand (§4.5).</summary>
    public string CheckDigits { get; set; } = "";

    /// <summary>The parent location (aisle → bay → level), or null at the top.</summary>
    public string? ParentId { get; set; }

    /// <summary>The materialized path of codes from the top, <c>/A/A-01/A-01-03/</c> (WX9 helpers), for prefix grouping.</summary>
    public string Path { get; set; } = "/";

    /// <summary>The position on the walk path; routes sort by it.</summary>
    public long WalkSequence { get; set; }

    public WarehouseLocationType Type { get; set; } = WarehouseLocationType.Storage;

    public string? Aisle { get; set; }
    public string? Bay { get; set; }
    public string? Level { get; set; }
    public string? Position { get; set; }
    public string? Side { get; set; }

    /// <summary>A capacity hint in base units of the fixed item, or null.</summary>
    public decimal? CapacityHint { get; set; }

    /// <summary>A fixed pick face's item (min/max replenishment applies only to fixed pick faces, §4.5).</summary>
    public string? FixedItemId { get; set; }

    /// <summary>Replenish when on hand drops to or below this (fixed pick faces only).</summary>
    public decimal? Min { get; set; }

    /// <summary>Replenish up to this.</summary>
    public decimal? Max { get; set; }

    /// <summary>False once deactivated; a location is never deleted once it has ledger history.</summary>
    public bool Active { get; set; } = true;

    /// <summary>The count class (ABC, Q-W8), or null.</summary>
    public string? CountClass { get; set; }

    /// <summary>Set by the first movement that touches the location; from then on it can only be deactivated.</summary>
    public bool HasHistory { get; set; }

    internal WarehouseLocationRef ToRef() => new(Id!, SiteId, Code, ZoneId, WalkSequence, Type,
        new LocationGeometry(Aisle, Bay, Level, Position, Side), Path);
}

/// <summary>Document-id helpers: codes are untrusted input (scans, CSV) and go into ids only normalised.</summary>
public static class WarehouseIds
{
    /// <summary>
    /// <paramref name="code"/> trimmed and upper-cased, refusing anything but letters, digits and <c>-_.</c>: an id never
    /// carries a <c>/</c> from input, so a code cannot reach into another document's id space.
    /// </summary>
    /// <exception cref="ArgumentException">The code is empty or holds another character.</exception>
    public static string Normalize(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        var trimmed = code.Trim().ToUpperInvariant();
        if (trimmed.Length == 0 || trimmed.Length > 64 || !trimmed.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            throw new ArgumentException($"'{code}' is not a valid code (letters, digits, '-', '_' and '.'; at most 64).", nameof(code));
        return trimmed;
    }

    /// <summary>A module name as the module registry spells it (case kept), checked against <c>^[A-Za-z0-9_.-]+$</c>.</summary>
    /// <exception cref="ArgumentException">The name is empty or holds another character.</exception>
    public static string Module(string module)
    {
        ArgumentNullException.ThrowIfNull(module);
        var trimmed = module.Trim();
        if (trimmed.Length == 0 || trimmed.Length > 128 || !trimmed.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            throw new ArgumentException($"'{module}' is not a valid module name.", nameof(module));
        return trimmed;
    }
}
