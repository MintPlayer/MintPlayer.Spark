using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Boards;

/// <summary>
/// What a tile can show (<c>docs/dashboard_layout_PRD.md</c> §4.4). The built-in kinds use this interface too. A kind is
/// resolved per request, so it can take scoped services (<c>IPermissionService</c>, <c>IDatabaseAccess</c>).
/// Register an application kind with <see cref="SparkBoardsOptions.AddKind{TKind}"/>; the client renders it with a
/// component registered through <c>provideSparkTileRenderers</c>.
/// </summary>
public interface ITileKind
{
    /// <summary>The kind's name in <c>boards.json</c> and on the wire.</summary>
    string Name { get; }

    /// <summary>Null when <paramref name="target"/> is well formed for this kind, else why not. Runs before any access check.</summary>
    string? Validate(IReadOnlyDictionary<string, string> target);

    /// <summary>
    /// Whether the caller may see a tile over <paramref name="target"/>, and the target's own caption. Called on every
    /// read and on every save. A denied tile is left out entirely, so a denial must not throw.
    /// </summary>
    Task<SparkTileAccess> CheckAccessAsync(IReadOnlyDictionary<string, string> target, CancellationToken cancellationToken);
}

/// <summary>The outcome of <see cref="ITileKind.CheckAccessAsync"/>.</summary>
public sealed record SparkTileAccess(bool Allowed, TranslatedString? Title = null)
{
    public static readonly SparkTileAccess Denied = new(false);

    public static SparkTileAccess Allow(TranslatedString? title = null) => new(true, title);
}
