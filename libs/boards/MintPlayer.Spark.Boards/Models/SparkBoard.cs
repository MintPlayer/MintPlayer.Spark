using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Boards;

/// <summary>
/// A board the application ships: <c>App_Data/boards.json</c> or <see cref="SparkBoardsOptions.Boards"/>. Its tiles
/// and layouts are the default every user starts from (<c>docs/dashboard_layout_PRD.md</c> §4.2).
/// </summary>
public sealed class SparkBoardDefinition
{
    /// <summary>The board's id, matching <see cref="SparkBoardIds.Pattern"/>: <c>home</c> by convention.</summary>
    public string Id { get; set; } = "";

    public TranslatedString? Title { get; set; }

    public List<SparkBoardTile> Tiles { get; set; } = [];

    /// <summary>Positions per column count, the way <c>bs-tile-manager</c>'s <c>layouts</c> holds them.</summary>
    public Dictionary<int, List<SparkTileLayoutEntry>> Layouts { get; set; } = [];
}

/// <summary>One tile: what it shows (<see cref="Kind"/> over <see cref="Target"/>), never where.</summary>
public sealed class SparkBoardTile
{
    /// <summary>Stable across releases: a user's layout refers to a default tile by this id.</summary>
    public string Id { get; set; } = "";

    /// <summary>A registered <see cref="ITileKind"/>: <c>query</c>, <c>count</c>, <c>persistentObject</c>, <c>programUnit</c> or an app kind.</summary>
    public string Kind { get; set; } = "";

    /// <summary>The kind's target, e.g. <c>{ "query": "Recent_Cars" }</c> or <c>{ "entityType": "Car", "id": "cars/1" }</c>.</summary>
    public Dictionary<string, string> Target { get; set; } = [];

    /// <summary>The application's caption. Null takes the target's own (the query's or the type's label).</summary>
    public TranslatedString? Title { get; set; }

    public SparkTileSize? DefaultSize { get; set; }
}

public sealed record SparkTileSize(int ColSpan, int RowSpan);

public sealed record SparkTilePosition(int ColStart, int RowStart, int ColSpan, int RowSpan);

public sealed record SparkTileLayoutEntry(string Id, SparkTilePosition Position);

/// <summary>A tile as <c>GET /spark/boards/{board}</c> sends it: visible to the caller, its caption resolved.</summary>
public sealed class SparkBoardTileView
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required IReadOnlyDictionary<string, string> Target { get; init; }

    /// <summary>The caption to show: the user's rename, else the application's, else the target's.</summary>
    public TranslatedString? Title { get; init; }

    /// <summary>The user's rename, or null. Kept apart from <see cref="Title"/> so the editor can clear it.</summary>
    public string? CustomTitle { get; init; }

    public SparkTileSize? DefaultSize { get; init; }

    /// <summary>True for an application tile, false for one the user added.</summary>
    public bool IsDefault { get; init; }
}

public sealed class SparkBoardView
{
    public required string Id { get; init; }
    public TranslatedString? Title { get; init; }
    public required IReadOnlyList<SparkBoardTileView> Tiles { get; init; }
    public required IReadOnlyDictionary<int, IReadOnlyList<SparkTileLayoutEntry>> Layouts { get; init; }

    /// <summary>The column counts the caller's own layout covers. A save sends these and the counts edited since.</summary>
    public IReadOnlyList<int> CustomizedCounts { get; init; } = [];

    /// <summary>
    /// The default tiles the caller removed and may still see. A save states every removal, so it sends these back
    /// (and the "Add tile" picker can offer them again). Removed defaults the caller cannot see are kept by the server.
    /// </summary>
    public IReadOnlyList<SparkBoardTileView> Removed { get; init; } = [];

    /// <summary>True when the caller has a stored layout (Reset has something to delete).</summary>
    public bool Customized { get; init; }

    /// <summary>True when the caller may save: signed in, and holding <c>Customize/Board</c> when the app requires it.</summary>
    public bool CanCustomize { get; init; }

    /// <summary>The stored document's change vector, sent back with the next save (D13).</summary>
    public string? ChangeVector { get; init; }
}

public static class SparkBoardIds
{
    /// <summary>A board id: lowercase, digits and dashes. It is the last segment of the layout's document id (D16).</summary>
    public const string Pattern = "^[a-z0-9][a-z0-9-]{0,63}$";

    private static readonly System.Text.RegularExpressions.Regex Regex = new(Pattern, System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static bool IsValid(string? id) => id is not null && Regex.IsMatch(id);
}
