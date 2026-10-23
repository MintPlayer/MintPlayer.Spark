namespace MintPlayer.Spark.Boards.Services;

/// <summary>
/// The pure half of a board (<c>docs/dashboard_layout_PRD.md</c> S3, §4.2, §4.7): what a user sees from the
/// application's default and their stored layout, and what a save stores. No I/O; the access checks are
/// passed in as the set of visible tile ids.
/// </summary>
internal static class BoardMerge
{
    public const int MaxColumns = 24;
    public const int MaxRows = 1000;
    public const int MaxTitleLength = 100;

    public sealed record MergedTile(SparkBoardTile Tile, bool IsDefault, string? CustomTitle);

    public sealed record Merged(IReadOnlyList<MergedTile> Tiles, IReadOnlyDictionary<int, IReadOnlyList<SparkTileLayoutEntry>> Layouts);

    /// <summary>
    /// The tiles and layouts before the access filter. Defaults the user removed stay removed, defaults the
    /// application removed disappear, and a default the user has not seen yet keeps the application's position
    /// where it is free (else it has none, and the tile manager appends it). The user's <c>layouts[n]</c> replaces
    /// the application's <c>layouts[n]</c>.
    /// </summary>
    public static Merged Merge(SparkBoardDefinition board, BoardLayout? user)
    {
        if (user is null)
            return new(
                board.Tiles.Select(t => new MergedTile(t, true, null)).ToList(),
                board.Layouts.ToDictionary(p => p.Key, p => (IReadOnlyList<SparkTileLayoutEntry>)p.Value.ToList()));

        var removed = user.Removed.ToHashSet(StringComparer.Ordinal);
        var seen = user.DefaultsSeen.ToHashSet(StringComparer.Ordinal);
        var tiles = board.Tiles.Where(t => !removed.Contains(t.Id)).Select(t => new MergedTile(t, true, TitleOf(user, t.Id)))
            .Concat(user.Tiles.Select(t => new MergedTile(t, false, TitleOf(user, t.Id))))
            .ToList();
        var ids = tiles.Select(t => t.Tile.Id).ToHashSet(StringComparer.Ordinal);
        var unseen = board.Tiles.Where(t => !removed.Contains(t.Id) && !seen.Contains(t.Id)).Select(t => t.Id).ToList();

        var layouts = new Dictionary<int, IReadOnlyList<SparkTileLayoutEntry>>();
        foreach (var count in board.Layouts.Keys.Union(user.Layouts.Keys))
        {
            if (!user.Layouts.TryGetValue(count, out var stored))
            {
                layouts[count] = board.Layouts[count].Where(e => ids.Contains(e.Id)).ToList();
                continue;
            }
            var entries = stored.Where(e => ids.Contains(e.Id)).ToList();
            if (board.Layouts.TryGetValue(count, out var defaults))
            {
                foreach (var id in unseen)
                {
                    if (entries.Any(e => e.Id == id) || defaults.FirstOrDefault(e => e.Id == id) is not { } entry)
                        continue;
                    if (!entries.Any(e => Overlaps(e.Position, entry.Position)))
                        entries.Add(entry);
                }
            }
            layouts[count] = entries;
        }
        return new(tiles, layouts);
    }

    /// <summary>Drops the tiles the caller may not see, and their positions, so nothing about them is sent (§4.3).</summary>
    public static Merged Filter(Merged merged, IReadOnlySet<string> visible)
        => new(
            merged.Tiles.Where(t => visible.Contains(t.Tile.Id)).ToList(),
            merged.Layouts.ToDictionary(p => p.Key, p => (IReadOnlyList<SparkTileLayoutEntry>)p.Value.Where(e => visible.Contains(e.Id)).ToList()));

    /// <summary>What a save sends: the caller's own tiles, removals, renames and the layouts of the counts they edited at.</summary>
    public sealed class SaveRequest
    {
        public List<SparkBoardTile> Tiles { get; set; } = [];
        public List<string> Removed { get; set; } = [];
        public Dictionary<string, string> Titles { get; set; } = [];
        public Dictionary<int, List<SparkTileLayoutEntry>> Layouts { get; set; } = [];
    }

    /// <summary>
    /// The document a save stores, or the reason it is refused. <paramref name="denied"/> are the ids of the stored
    /// and default tiles the caller cannot see now: they were never sent, so their absence from the request means
    /// nothing, and their stored tile, removal, rename and positions are kept (§4.7). Tiles in the request must
    /// be visible to the caller (<paramref name="allowedNew"/> holds the user tiles that passed the access check).
    /// </summary>
    public static (BoardLayout? Layout, string? Error) Apply(SparkBoardDefinition board, BoardLayout? stored, SaveRequest request,
        IReadOnlySet<string> denied, IReadOnlySet<string> allowedNew, int maxUserTiles)
    {
        var defaults = board.Tiles.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        stored ??= new BoardLayout();

        // The user's tiles.
        var userTiles = new List<SparkBoardTile>();
        var userIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tile in request.Tiles)
        {
            if (!TileIds.IsValid(tile.Id) || !tile.Id.StartsWith(TileIds.UserPrefix, StringComparison.Ordinal))
                return (null, $"tile id '{tile.Id}' is not a user tile id ({TileIds.UserPrefix}…)");
            if (!userIds.Add(tile.Id))
                return (null, $"tile '{tile.Id}' appears twice");
            if (!allowedNew.Contains(tile.Id))
                return (null, $"tile '{tile.Id}' names a target you cannot access");
            userTiles.Add(new SparkBoardTile { Id = tile.Id, Kind = tile.Kind, Target = new(tile.Target, StringComparer.Ordinal), DefaultSize = tile.DefaultSize });
        }
        foreach (var kept in stored.Tiles.Where(t => denied.Contains(t.Id) && !userIds.Contains(t.Id)))
        {
            userTiles.Add(kept);
            userIds.Add(kept.Id);
        }
        if (userTiles.Count > maxUserTiles)
            return (null, $"a board holds at most {maxUserTiles} tiles of your own");

        // Removed defaults.
        var removed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in request.Removed)
        {
            if (!defaults.Contains(id) || denied.Contains(id))
                return (null, $"'{id}' is not a tile of this board");
            removed.Add(id);
        }
        removed.UnionWith(stored.Removed.Where(id => defaults.Contains(id) && denied.Contains(id)));

        bool IsLive(string id) => (defaults.Contains(id) && !removed.Contains(id)) || userIds.Contains(id);
        bool IsSendable(string id) => IsLive(id) && !denied.Contains(id);

        // Renames.
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, title) in request.Titles)
        {
            if (!IsSendable(id))
                return (null, $"'{id}' is not a tile of this board");
            var trimmed = title?.Trim() ?? "";
            if (trimmed.Length > MaxTitleLength)
                return (null, $"a tile title is at most {MaxTitleLength} characters");
            if (trimmed.Length > 0)
                titles[id] = trimmed;
        }
        foreach (var (id, title) in stored.Titles.Where(p => denied.Contains(p.Key) && IsLive(p.Key)))
            titles.TryAdd(id, title);

        // Layouts, per count.
        var layouts = new Dictionary<int, List<SparkTileLayoutEntry>>();
        foreach (var (count, entries) in request.Layouts)
        {
            if (count is < 1 or > MaxColumns)
                return (null, $"a layout for {count} columns is not allowed (1 to {MaxColumns})");
            var placed = new HashSet<string>(StringComparer.Ordinal);
            var list = new List<SparkTileLayoutEntry>();
            foreach (var entry in entries ?? [])
            {
                if (entry?.Id is null || !IsSendable(entry.Id))
                    return (null, $"layout {count} places '{entry?.Id}', which is not a tile of this board");
                if (entry.Position is null || !IsValidPosition(entry.Position, count))
                    return (null, $"layout {count} places '{entry.Id}' outside {count} columns");
                if (!placed.Add(entry.Id))
                    return (null, $"layout {count} places '{entry.Id}' twice");
                list.Add(new SparkTileLayoutEntry(entry.Id, entry.Position));
            }
            // Before a first save of this count, the hidden tiles keep their default positions.
            if (stored.Layouts.TryGetValue(count, out var previous) || board.Layouts.TryGetValue(count, out previous))
                list.AddRange(previous.Where(e => denied.Contains(e.Id) && IsLive(e.Id) && placed.Add(e.Id)));
            layouts[count] = list;
        }

        return (new BoardLayout
        {
            Board = board.Id,
            DefaultsSeen = board.Tiles.Select(t => t.Id).ToList(),
            Tiles = userTiles,
            Removed = removed.Order(StringComparer.Ordinal).ToList(),
            Titles = titles,
            Layouts = layouts,
        }, null);
    }

    public static bool IsValidPosition(SparkTilePosition position, int columns)
        => position.ColStart >= 1 && position.ColSpan >= 1 && position.ColStart + position.ColSpan - 1 <= columns
            && position.RowStart >= 1 && position.RowSpan >= 1 && position.RowStart + position.RowSpan - 1 <= MaxRows;

    private static bool Overlaps(SparkTilePosition a, SparkTilePosition b)
        => a.ColStart < b.ColStart + b.ColSpan && b.ColStart < a.ColStart + a.ColSpan
            && a.RowStart < b.RowStart + b.RowSpan && b.RowStart < a.RowStart + a.RowSpan;

    private static string? TitleOf(BoardLayout user, string id) => user.Titles.TryGetValue(id, out var title) ? title : null;
}
