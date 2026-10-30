namespace MintPlayer.Spark.Boards;

/// <summary>Bound from <c>Spark:Boards</c>; <c>spark.AddBoards(o =&gt; …)</c> configures the rest.</summary>
public sealed class SparkBoardsOptions
{
    public const string SectionName = "Spark:Boards";

    /// <summary>The right that saving needs when <see cref="RequireCustomizeRight"/> is set: <c>Customize/Board</c>.</summary>
    public const string CustomizeAction = "Customize";
    public const string CustomizeTarget = "Board";

    /// <summary>When true, saving and resetting need <c>Customize/Board</c>. By default every signed-in user may (D8).</summary>
    public bool RequireCustomizeRight { get; set; }

    /// <summary>Boards declared in code, beside <c>App_Data/boards.json</c>. An id declared in both refuses startup.</summary>
    public List<SparkBoardDefinition> Boards { get; } = [];

    /// <summary>
    /// The most tiles one board holds, its declared and the user's own together (docs/spark_ssr_PRD.md D14). A declared
    /// board over it refuses startup; a save over it is a 400. Bounds the stored document and a page's fan-out.
    /// </summary>
    public int MaxTiles { get; set; } = 20;

    /// <summary>
    /// The most tiles a server-rendered board supplies the content of (D14). A board with more renders its query and
    /// object tiles as links until the page hydrates. ⚠️ Equals <c>SPARK_BOARD_PRERENDERED_TILES</c> in
    /// <c>@mintplayer/ng-spark/boards</c>, which renders the links.
    /// </summary>
    public const int PrerenderedTileLimit = 7;

    internal List<Type> KindTypes { get; } = [];

    /// <summary>Registers an application tile kind. It is created per request, so it may take scoped services.</summary>
    public SparkBoardsOptions AddKind<TKind>() where TKind : class, ITileKind
    {
        if (!KindTypes.Contains(typeof(TKind)))
            KindTypes.Add(typeof(TKind));
        return this;
    }
}
