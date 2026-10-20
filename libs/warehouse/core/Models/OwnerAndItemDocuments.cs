using MintPlayer.Spark.Organizations;

namespace MintPlayer.Spark.Warehouse.Models;

/// <summary>
/// An owner link (Q-W23): maps (source module, the owner id that module uses) to the warehouse organization that owns the
/// goods. Organization ids differ per database, so every contract crossing the boundary names the owner as its producer
/// knows it (<c>OwnerId</c>), and the warehouse translates through this link.
/// <list type="bullet">
/// <item>a supervisor creates it; a single-owner pair gets it automatically (<see cref="Automatic"/>);</item>
/// <item>orders from an unlinked owner are <c>Rejected(OwnerNotServed)</c>;</item>
/// <item>the link also filters what a module receives back: status, shipments, stock levels and return receipts only for
/// its linked owner ids (<c>WarehouseOwnerLinkRowFilter</c>, replication's row filter, PRD §4.2.3).</item>
/// </list>
/// Id <c>warehouse-owner-links/{sourceModule}/{externalOwnerId}</c>.
/// </summary>
public sealed class WarehouseOwnerLink
{
    public const string IdPrefix = "warehouse-owner-links/";

    public static string IdFor(string sourceModule, string externalOwnerId)
        => $"{IdPrefix}{WarehouseIds.Module(sourceModule)}/{Uri.EscapeDataString(externalOwnerId.Trim())}";

    public string? Id { get; set; }

    /// <summary>The module (shop) the owner id belongs to, as named in the module registry.</summary>
    public string SourceModule { get; set; } = "";

    /// <summary>The owner id as that module writes it into <c>OwnerId</c>.</summary>
    public string ExternalOwnerId { get; set; } = "";

    /// <summary>The warehouse <see cref="Organizations.Models.Organization"/> that owns the goods.</summary>
    public string OrganizationId { get; set; } = "";

    /// <summary>The site orders of this owner go to when no routing rule matches (WD3), or null.</summary>
    public string? DefaultSiteId { get; set; }

    /// <summary>Created by the system for a single-owner pair, not by a supervisor.</summary>
    public bool Automatic { get; set; }

    public bool Active { get; set; } = true;
}

/// <summary>Whether an item is tracked by lot (PRD §4.5: none, or lot + expiry).</summary>
public enum ItemTracking
{
    None,
    LotAndExpiry,
}

/// <summary>A lithium battery's packaging (round 5, commerce GC9; IATA PI 965–970 kinds).</summary>
public enum LithiumBatteryKind
{
    None,
    InEquipment,
    WithEquipment,
    Alone,
}

/// <summary>
/// Dangerous-goods data (round 5, commerce GC9): restricts carrier services at packing (§4.7) and may need a segregated
/// zone (<see cref="ZoneMixingRules"/>). Classification under ADR/IATA is the owner's responsibility; confirm per carrier.
/// </summary>
public sealed class DangerousGoodsData
{
    /// <summary>The UN number, four digits, e.g. <c>3481</c>.</summary>
    public string? UnNumber { get; set; }

    /// <summary>The hazard class or division, e.g. <c>9</c> or <c>2.1</c>.</summary>
    public string? HazardClass { get; set; }

    /// <summary>I, II or III, or null.</summary>
    public string? PackingGroup { get; set; }

    public LithiumBatteryKind LithiumBattery { get; set; }

    public bool LimitedQuantity { get; set; }
}

/// <summary>A packaging level of an item and how many base units it holds (PRD §4.5; WX8: quantities in base units).</summary>
public sealed class ItemPackaging
{
    public PackagingLevel Level { get; set; }

    /// <summary>Base units per package (1 for <see cref="PackagingLevel.Each"/>).</summary>
    public decimal Quantity { get; set; } = 1;
}

/// <summary>
/// The warehouse's item (PRD §4.5 "Items", WD9): it extends the shop's <c>catalog-items</c> replica with dimensions,
/// packaging, storage and lot policy, or stands alone for goods nobody sells. Its id never changes, so balances and the
/// ledger keep pointing at it when a pre-item is linked to the shop's item later (Q-W12: the link may re-key the SKU,
/// audited). (owner, SKU) is a unique key, held by a compare-exchange reservation (<c>WarehouseItemKeys</c>).
/// </summary>
public sealed class WarehouseItem : IOrganizationOwned
{
    public const string IdPrefix = "warehouse-items/";

    public string? Id { get; set; }

    /// <summary>The owner of the goods (WD3, WI5).</summary>
    public string? OrganizationId { get; set; }

    public string Sku { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>The <c>FulfilmentCatalogItem</c> replica this item extends, or null (warehouse-owned or a pre-item).</summary>
    public string? CatalogItemId { get; set; }

    /// <summary>Created before the shop's item existed (Q-W12); linked later without moving stock.</summary>
    public bool IsPreItem { get; set; }

    /// <summary>The replica it extends was tombstoned: discontinued at the shop, kept while stock remains (WD9).</summary>
    public bool CatalogItemRemoved { get; set; }

    public decimal? LengthMm { get; set; }
    public decimal? WidthMm { get; set; }
    public decimal? HeightMm { get; set; }
    public decimal? WeightGrams { get; set; }

    public List<ItemPackaging> Packaging { get; set; } = [new() { Level = PackagingLevel.Each, Quantity = 1 }];

    /// <summary>Free text: ambient, chilled, frozen, fragile…</summary>
    public string? StorageRequirements { get; set; }

    public ItemTracking Tracking { get; set; }

    /// <summary>FEFO sorts by expiry minus this (Odoo's removal date, §4.5).</summary>
    public int MinimumShelfLifeDays { get; set; }

    /// <summary>The allocation rule for this item (<c>Fifo</c>, <c>Fefo</c>, <c>ClosestLocation</c> or a custom name), or null for the default.</summary>
    public string? RemovalRule { get; set; }

    public string? DefaultLocationId { get; set; }

    public List<string> FixedLocationIds { get; set; } = [];

    /// <summary>Round 5 (commerce GC9): null when the item is not dangerous goods.</summary>
    public DangerousGoodsData? DangerousGoods { get; set; }

    public bool Active { get; set; } = true;

    /// <summary>The base units one scan of <paramref name="level"/> counts.</summary>
    public decimal UnitsPer(PackagingLevel level)
        => Packaging.FirstOrDefault(p => p.Level == level)?.Quantity ?? (level == PackagingLevel.Each ? 1 : throw new InvalidOperationException($"Item {Sku} has no {level} packaging."));
}

/// <summary>
/// A barcode registration (PRD §4.6, WD8): one code may map to several items, because vendor codes collide across owners
/// and suppliers. Id <c>barcodes/{code}/{ownerKey}/{itemKey}</c>, so all registrations of a code load by prefix without
/// an index.
/// </summary>
public sealed class BarcodeRegistration : IOrganizationOwned
{
    public const string IdPrefix = "barcodes/";

    /// <summary>The prefix of every registration of <paramref name="normalizedCode"/>.</summary>
    public static string PrefixFor(string normalizedCode) => $"{IdPrefix}{normalizedCode}/";

    public static string IdFor(string normalizedCode, string organizationId, string itemId)
        => $"{PrefixFor(normalizedCode)}{Uri.EscapeDataString(organizationId)}/{Uri.EscapeDataString(itemId)}";

    public string? Id { get; set; }

    /// <summary>The normalised code: GTINs as 14 digits, other codes trimmed and upper-cased.</summary>
    public string Code { get; set; } = "";

    public BarcodeCodeType CodeType { get; set; }

    public PackagingLevel Level { get; set; }

    /// <summary>Base units one scan counts.</summary>
    public decimal Quantity { get; set; } = 1;

    public string? OrganizationId { get; set; }

    public string ItemId { get; set; } = "";
}
