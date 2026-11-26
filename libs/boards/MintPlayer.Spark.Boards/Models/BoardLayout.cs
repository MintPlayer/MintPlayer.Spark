using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Boards;

/// <summary>
/// One user's changes to one board (<c>docs/dashboard_layout_PRD.md</c> §4.2). Its id is natural,
/// <c>boardlayouts/{userId}/{boardId}</c>: a load is a point lookup, a second save overwrites, and Reset deletes it.
/// Layouts stored before #536 keep their <c>BoardLayouts/</c> casing; RavenDB ids ignore case, so they still load.
/// </summary>
[IdKind(SparkIdKind.Natural)]
public sealed partial class BoardLayout
{
    public const string IdPrefix = "boardlayouts/";

    public Id<BoardLayout> Id { get; set; }

    public string Owner { get; set; } = "";

    public string Board { get; set; } = "";

    /// <summary>The default tiles this user's layout already accounts for. A default not listed is new to them.</summary>
    public List<string> DefaultsSeen { get; set; } = [];

    /// <summary>Tiles the user added, in their own id space (<c>u-…</c>).</summary>
    public List<SparkBoardTile> Tiles { get; set; } = [];

    /// <summary>Default tiles the user removed. They stay removed when the application changes the default.</summary>
    public List<string> Removed { get; set; } = [];

    /// <summary>The user's renames, by tile id.</summary>
    public Dictionary<string, string> Titles { get; set; } = [];

    /// <summary>Only the column counts the user edited at. A count not here is the application's (§4.7).</summary>
    public Dictionary<int, List<SparkTileLayoutEntry>> Layouts { get; set; } = [];

    public static Id<BoardLayout> IdFor(string userId, string boardId) => Id<BoardLayout>.FromTrusted(IdPrefix + userId + "/" + boardId);
}
