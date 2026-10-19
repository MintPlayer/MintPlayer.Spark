using System.Text.Json.Serialization;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Commerce.Ticketing.Venues.Models;

// The geometry of a seating layout (PRD §4.11, D7, D32): what the editor edits, what a snapshot freezes, what the
// seat map draws. One shape on the server, in the database and in the browser (the TypeScript contract is generated
// from these types). Coordinates are plan units (y grows downwards), not pixels: the renderer scales them.

/// <summary>
/// A seating layout's plan: blocks with rows and seats, standing areas, decor, price categories and fill chains, over
/// an optional background image.
/// </summary>
[SparkContract]
public sealed class LayoutPlan
{
    /// <summary>The plan's width in plan units.</summary>
    public double Width { get; set; } = 1000;

    /// <summary>The plan's height in plan units.</summary>
    public double Height { get; set; } = 1000;

    /// <summary>The uploaded background (an architect's drawing) the blocks are traced over, if any.</summary>
    public LayoutBackground? Background { get; set; }

    /// <summary>The price categories seats and blocks belong to. Prices themselves are the sale's (Ticketing.Events).</summary>
    public List<LayoutCategory> Categories { get; set; } = [];

    public List<LayoutBlock> Blocks { get; set; } = [];

    /// <summary>Shapes that sell nothing: the stage, a mixing desk, pillars, labels.</summary>
    public List<LayoutDecor> Decor { get; set; } = [];

    /// <summary>The fill order of best available (D25): one chain per block group.</summary>
    public List<LayoutChain> Chains { get; set; } = [];
}

/// <summary>
/// Where the background image lies on the plan. The image is an immutable <c>VenueLayoutBackgrounds/…</c> document
/// (its attachment), so drafts, published versions and snapshots share it without copying.
/// </summary>
[SparkContract]
public sealed class LayoutBackground
{
    /// <summary>The uploaded image (<c>POST /layouts/background</c> answers it).</summary>
    public string ImageId { get; set; } = "";

    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    /// <summary>0 (invisible) to 1. Default 0.5, so the seats stay readable over it.</summary>
    public double Opacity { get; set; } = 0.5;
}

/// <summary>A price category ("Cat 1"); the sale gives it a price per ticket type.</summary>
[SparkContract]
public sealed class LayoutCategory
{
    /// <summary>Stable key, <see cref="LayoutKeys.Pattern"/>.</summary>
    public string Key { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary><c>#rrggbb</c>.</summary>
    public string Color { get; set; } = "#3b82f6";
}

/// <summary>The three block kinds of D7.</summary>
[SparkContract]
[JsonConverter(typeof(JsonStringEnumConverter<BlockKind>))]
public enum BlockKind
{
    /// <summary>A standing area with a capacity: quota only.</summary>
    [JsonStringEnumMemberName("standing")] Standing,

    /// <summary>Seated without numbers (free seating): quota only.</summary>
    [JsonStringEnumMemberName("seatedUnnumbered")] SeatedUnnumbered,

    /// <summary>Numbered seats, each a unit of the inventory engine.</summary>
    [JsonStringEnumMemberName("seatedNumbered")] SeatedNumbered,
}

/// <summary>A block (a stand, a balcony section, a standing pit): a polygon, and rows of seats when numbered.</summary>
[SparkContract]
public sealed class LayoutBlock
{
    /// <summary>Stable key, <see cref="LayoutKeys.Pattern"/>; part of the snapshot's quota id.</summary>
    public string Key { get; set; } = "";

    public string Name { get; set; } = "";

    public BlockKind Kind { get; set; } = BlockKind.SeatedNumbered;

    /// <summary>The outline as a flat list <c>x0, y0, x1, y1, …</c> (at least three points).</summary>
    public List<double> Polygon { get; set; } = [];

    /// <summary>How many people a standing or unnumbered block holds. Ignored for numbered blocks (their seats count).</summary>
    public int Capacity { get; set; }

    /// <summary>The block's price category; a seat may override it.</summary>
    public string? Category { get; set; }

    /// <summary>Rows front to back (towards the stage first), each with its seats in seat order (aisle to aisle).</summary>
    public List<LayoutRow> Rows { get; set; } = [];
}

[SparkContract]
public sealed class LayoutRow
{
    /// <summary>Stable key within the block, <see cref="LayoutKeys.Pattern"/>.</summary>
    public string Key { get; set; } = "";

    /// <summary>What the ticket prints: <c>A</c>, <c>12</c>.</summary>
    public string Label { get; set; } = "";

    /// <summary>Seats in seat order, aisle to aisle: the order the single-empty-seat rule reads (§4.3).</summary>
    public List<LayoutSeat> Seats { get; set; } = [];
}

[SparkContract]
public sealed class LayoutSeat
{
    /// <summary>Stable key, unique in the whole plan (<see cref="LayoutKeys.Pattern"/>); part of the seat's unit id.</summary>
    public string Key { get; set; } = "";

    /// <summary>What the ticket prints: <c>14</c>.</summary>
    public string Label { get; set; } = "";

    /// <summary>The seat's centre.</summary>
    public double X { get; set; }

    public double Y { get; set; }

    /// <summary><see cref="SeatTags"/> flags: accessible, companion, restricted view (default 3).</summary>
    public int Tags { get; set; }

    /// <summary>Overrides the block's price category (a restricted-view seat sold cheaper).</summary>
    public string? Category { get; set; }

    /// <summary>For a companion seat: the accessible seat it belongs to (default 3). Empty: the adjacent accessible seat.</summary>
    public string? CompanionOf { get; set; }
}

/// <summary>The seat tags (default 3): flags in <see cref="LayoutSeat.Tags"/>.</summary>
public static class SeatTags
{
    /// <summary>A wheelchair space: sold only with a ticket type that allows it.</summary>
    public const int Accessible = 1;

    /// <summary>The companion seat beside a wheelchair space: sold with it, or with a ticket type that allows it.</summary>
    public const int Companion = 2;

    /// <summary>The view is restricted: shown to the buyer, usually a cheaper category; no restriction on sale.</summary>
    public const int RestrictedView = 4;

    /// <summary>Every known flag.</summary>
    public const int All = Accessible | Companion | RestrictedView;

    /// <summary>The tags that restrict who may buy the seat.</summary>
    public const int Restricting = Accessible | Companion;

    /// <summary>The CSV names, in flag order.</summary>
    public static readonly IReadOnlyList<(int Flag, string Name)> Names =
    [
        (Accessible, "accessible"),
        (Companion, "companion"),
        (RestrictedView, "restricted-view"),
    ];
}

[SparkContract]
[JsonConverter(typeof(JsonStringEnumConverter<DecorKind>))]
public enum DecorKind
{
    [JsonStringEnumMemberName("polygon")] Polygon,
    [JsonStringEnumMemberName("stage")] Stage,
    [JsonStringEnumMemberName("text")] Text,
}

/// <summary>A shape that sells nothing.</summary>
[SparkContract]
public sealed class LayoutDecor
{
    public string Key { get; set; } = "";

    public DecorKind Kind { get; set; }

    /// <summary>Flat <c>x0, y0, …</c>; a text has one point (its anchor).</summary>
    public List<double> Points { get; set; } = [];

    public string? Label { get; set; }

    /// <summary><c>#rrggbb</c>, or empty for the theme's decor colour.</summary>
    public string? Color { get; set; }
}

/// <summary>
/// A fill chain (D25): numbered blocks in fill order. Best available seats a group in the next contiguous run along
/// it, so groups sit together and the venue fills from the best blocks outwards. One chain per block group is the
/// engine's contention lever (§4.3).
/// </summary>
[SparkContract]
public sealed class LayoutChain
{
    public string Key { get; set; } = "";

    public string Name { get; set; } = "";

    public List<LayoutChainBlock> Blocks { get; set; } = [];
}

/// <summary>One block of a chain and its direction: rows front to back unless reversed, seats in row order unless reversed.</summary>
[SparkContract]
public sealed class LayoutChainBlock
{
    public string BlockKey { get; set; } = "";

    /// <summary>Fill the back row first.</summary>
    public bool BackToFront { get; set; }

    /// <summary>Fill each row from its last seat.</summary>
    public bool ReverseSeats { get; set; }
}
