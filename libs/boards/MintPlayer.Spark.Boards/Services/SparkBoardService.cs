using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Boards.Kinds;
using Raven.Client.Documents;
using Raven.Client.Exceptions;

namespace MintPlayer.Spark.Boards.Services;

/// <summary>The registered kinds for this request: the built-in four and the application's (<see cref="SparkBoardsOptions.AddKind{TKind}"/>).</summary>
internal sealed partial class SparkTileKindRegistry
{
    [Inject] private readonly IServiceProvider services;
    [Inject] private readonly IOptions<SparkBoardsOptions> options;

    private IReadOnlyDictionary<string, ITileKind>? kinds;

    public IReadOnlyDictionary<string, ITileKind> Kinds => kinds ??= Build();

    public ITileKind? Find(string? name) => name is not null && Kinds.TryGetValue(name, out var kind) ? kind : null;

    private Dictionary<string, ITileKind> Build()
    {
        var all = new Dictionary<string, ITileKind>(StringComparer.Ordinal);
        IEnumerable<ITileKind> builtIn =
        [
            ActivatorUtilities.CreateInstance<QueryTileKind>(services),
            ActivatorUtilities.CreateInstance<CountTileKind>(services),
            ActivatorUtilities.CreateInstance<PersistentObjectTileKind>(services),
            ActivatorUtilities.CreateInstance<ProgramUnitTileKind>(services),
        ];
        // An application kind with a built-in name replaces it.
        foreach (var kind in builtIn.Concat(options.Value.KindTypes.Select(t => (ITileKind)ActivatorUtilities.CreateInstance(services, t))))
            all[kind.Name] = kind;
        return all;
    }
}

internal enum BoardSaveOutcome { Saved, NotFound, Unauthorized, Forbidden, Invalid, Conflict }

internal sealed record BoardSaveResult(BoardSaveOutcome Outcome, string? Error = null, SparkBoardView? Board = null);

/// <summary>Reads, saves and resets a caller's board. Every tile passes its kind's access check on every read and save.</summary>
internal sealed partial class SparkBoardService
{
    [Inject] private readonly SparkBoardRegistry registry;
    [Inject] private readonly SparkTileKindRegistry kinds;
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly IPermissionService permissionService;
    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly IOptions<SparkBoardsOptions> options;

    /// <summary>The caller's board, or null when no such board is declared.</summary>
    public async Task<SparkBoardView?> GetAsync(string boardId, CancellationToken cancellationToken)
    {
        if (registry.Find(boardId) is not { } board)
            return null;
        BoardLayout? stored = null;
        string? changeVector = null;
        if (UserId() is { } userId)
        {
            using var session = store.OpenAsyncSession();
            stored = await session.LoadAsync<BoardLayout>(BoardLayout.IdFor(userId, board.Id), cancellationToken);
            if (stored is not null)
                changeVector = session.Advanced.GetChangeVectorFor(stored);
        }
        return await ViewAsync(board, stored, changeVector, cancellationToken);
    }

    public async Task<BoardSaveResult> SaveAsync(string boardId, BoardMerge.SaveRequest request, string? changeVector, CancellationToken cancellationToken)
    {
        if (registry.Find(boardId) is not { } board)
            return new(BoardSaveOutcome.NotFound);
        if (UserId() is not { } userId)
            return new(BoardSaveOutcome.Unauthorized);
        if (!await CanCustomizeAsync(cancellationToken))
            return new(BoardSaveOutcome.Forbidden);

        using var session = store.OpenAsyncSession();
        // A first save is stored as "must not exist", so two first saves race to one winner too.
        session.Advanced.OptimisticConcurrencyMode = Raven.Client.Documents.Session.OptimisticConcurrencyMode.Writes;
        var id = BoardLayout.IdFor(userId, board.Id);
        var stored = await session.LoadAsync<BoardLayout>(id, cancellationToken);
        if (stored is not null && session.Advanced.GetChangeVectorFor(stored) != changeVector)
            return new(BoardSaveOutcome.Conflict, "the board was changed elsewhere; reload it");

        // Every new or kept user tile is checked now, against the kind it names.
        var allowedNew = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tile in request.Tiles)
        {
            if (kinds.Find(tile.Kind) is not { } kind)
                return new(BoardSaveOutcome.Invalid, $"tile '{tile.Id}' has an unknown kind '{tile.Kind}'");
            tile.Target ??= [];
            if (tile.Target.Count > 8 || tile.Target.Any(p => p.Key.Length > 64 || (p.Value?.Length ?? 0) > 512))
                return new(BoardSaveOutcome.Invalid, $"tile '{tile.Id}' has an oversized target");
            if (kind.Validate(tile.Target) is { } error)
                return new(BoardSaveOutcome.Invalid, $"tile '{tile.Id}': {error}");
            if ((await kind.CheckAccessAsync(tile.Target, cancellationToken)).Allowed)
                allowedNew.Add(tile.Id);
        }
        var denied = await DeniedAsync(board.Tiles.Concat(stored?.Tiles ?? []), cancellationToken);

        var (layout, problem) = BoardMerge.Apply(board, stored, request, denied, allowedNew, options.Value.MaxUserTiles);
        if (layout is null)
            return new(BoardSaveOutcome.Invalid, problem);
        layout.Id = id;
        layout.Owner = userId;

        if (stored is not null)
        {
            stored.Board = layout.Board;
            stored.Owner = layout.Owner;
            stored.DefaultsSeen = layout.DefaultsSeen;
            stored.Tiles = layout.Tiles;
            stored.Removed = layout.Removed;
            stored.Titles = layout.Titles;
            stored.Layouts = layout.Layouts;
            layout = stored;
        }
        else
        {
            await session.StoreAsync(layout, id, cancellationToken);
        }
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyException)
        {
            return new(BoardSaveOutcome.Conflict, "the board was changed elsewhere; reload it");
        }
        return new(BoardSaveOutcome.Saved, Board: await ViewAsync(board, layout, session.Advanced.GetChangeVectorFor(layout), cancellationToken));
    }

    /// <summary>Deletes the caller's layout: the board is the application's default again.</summary>
    public async Task<BoardSaveResult> ResetAsync(string boardId, CancellationToken cancellationToken)
    {
        if (registry.Find(boardId) is not { } board)
            return new(BoardSaveOutcome.NotFound);
        if (UserId() is not { } userId)
            return new(BoardSaveOutcome.Unauthorized);
        if (!await CanCustomizeAsync(cancellationToken))
            return new(BoardSaveOutcome.Forbidden);
        using (var session = store.OpenAsyncSession())
        {
            session.Delete(BoardLayout.IdFor(userId, board.Id));
            await session.SaveChangesAsync(cancellationToken);
        }
        return new(BoardSaveOutcome.Saved, Board: await ViewAsync(board, null, null, cancellationToken));
    }

    public async Task<bool> CanCustomizeAsync(CancellationToken cancellationToken)
        => UserId() is not null
            && (!options.Value.RequireCustomizeRight
                || await permissionService.IsAllowedAsync(SparkBoardsOptions.CustomizeAction, SparkBoardsOptions.CustomizeTarget, cancellationToken));

    internal string? UserId() => currentUser.IsAuthenticated && currentUser.Id is { Length: > 0 } id ? id : null;

    private async Task<SparkBoardView> ViewAsync(SparkBoardDefinition board, BoardLayout? stored, string? changeVector, CancellationToken cancellationToken)
    {
        var merged = BoardMerge.Merge(board, stored);
        var visible = new HashSet<string>(StringComparer.Ordinal);
        var titles = new Dictionary<string, TranslatedString?>(StringComparer.Ordinal);
        foreach (var tile in merged.Tiles)
        {
            if (await CheckAsync(tile.Tile, cancellationToken) is { Allowed: true } access)
            {
                visible.Add(tile.Tile.Id);
                titles[tile.Tile.Id] = tile.CustomTitle is { } custom ? TranslatedString.Create(custom) : registry.Resolve(tile.Tile.Title) ?? access.Title;
            }
        }
        var filtered = BoardMerge.Filter(merged, visible);
        var removed = new List<SparkBoardTileView>();
        foreach (var tile in board.Tiles.Where(t => stored?.Removed.Contains(t.Id) == true))
        {
            if (await CheckAsync(tile, cancellationToken) is { Allowed: true } access)
                removed.Add(new SparkBoardTileView
                {
                    Id = tile.Id,
                    Kind = tile.Kind,
                    Target = tile.Target,
                    Title = registry.Resolve(tile.Title) ?? access.Title,
                    DefaultSize = tile.DefaultSize,
                    IsDefault = true,
                });
        }
        return new SparkBoardView
        {
            Id = board.Id,
            Title = registry.Resolve(board.Title),
            Tiles = filtered.Tiles.Select(t => new SparkBoardTileView
            {
                Id = t.Tile.Id,
                Kind = t.Tile.Kind,
                Target = t.Tile.Target,
                Title = titles[t.Tile.Id],
                CustomTitle = t.CustomTitle,
                DefaultSize = t.Tile.DefaultSize,
                IsDefault = t.IsDefault,
            }).ToList(),
            Layouts = filtered.Layouts,
            CustomizedCounts = stored?.Layouts.Keys.Order().ToList() ?? [],
            Removed = removed,
            Customized = stored is not null,
            CanCustomize = await CanCustomizeAsync(cancellationToken),
            ChangeVector = changeVector,
        };
    }

    private async Task<HashSet<string>> DeniedAsync(IEnumerable<SparkBoardTile> tiles, CancellationToken cancellationToken)
    {
        var denied = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tile in tiles)
            if (!(await CheckAsync(tile, cancellationToken)).Allowed)
                denied.Add(tile.Id);
        return denied;
    }

    /// <summary>An unknown kind or a malformed target is denied, like a target the caller cannot access.</summary>
    private async Task<SparkTileAccess> CheckAsync(SparkBoardTile tile, CancellationToken cancellationToken)
        => kinds.Find(tile.Kind) is { } kind && kind.Validate(tile.Target) is null
            ? await kind.CheckAccessAsync(tile.Target, cancellationToken)
            : SparkTileAccess.Denied;
}
