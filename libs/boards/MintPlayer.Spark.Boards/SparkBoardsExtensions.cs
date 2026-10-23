using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Boards.Services;

namespace MintPlayer.Spark.Boards;

/// <summary>
/// Boards, the dashboards users arrange (<c>docs/dashboard_layout_PRD.md</c>):
/// <code>
/// builder.Services.AddSpark(builder.Configuration, spark =>
/// {
///     spark.AddBoards();   // App_Data/boards.json, Spark:Boards
/// });
/// </code>
/// </summary>
public static class SparkBoardsExtensions
{
    /// <summary>
    /// Registers the <c>/spark/boards</c> endpoints, the four built-in tile kinds and the boards of
    /// <c>App_Data/boards.json</c>. A malformed board or an unknown kind refuses startup.
    /// </summary>
    public static ISparkBuilder AddBoards(this ISparkBuilder builder, Action<SparkBoardsOptions>? configure = null)
    {
        var services = builder.Services;
        if (services.Any(d => d.ServiceType == typeof(BoardsMarker)))
        {
            if (configure is not null)
                services.Configure(configure);
            return builder;
        }
        services.AddSingleton<BoardsMarker>();

        services.AddOptions<SparkBoardsOptions>();
        services.AddSingleton<IConfigureOptions<SparkBoardsOptions>>(sp => new ConfigureNamedOptions<SparkBoardsOptions>(
            Options.DefaultName,
            options => sp.GetService<IConfiguration>()?.GetSection(SparkBoardsOptions.SectionName).Bind(options)));
        if (configure is not null)
            services.Configure(configure);

        services.TryAddSingleton<SparkBoardRegistry>();
        services.TryAddScoped<SparkTileKindRegistry>();
        services.TryAddScoped<SparkBoardService>();
        services.TryAddScoped<SparkBoardCatalogBuilder>();

        builder.Registry.AddMiddleware(app => ValidateStartup(app.ApplicationServices));
        builder.Registry.AddEndpoints(endpoints => endpoints.MapSparkBoardsEndpoints());
        return builder;
    }

    /// <summary>Reads every board now and checks each tile's kind and target shape against the registered kinds.</summary>
    internal static void ValidateStartup(IServiceProvider services)
    {
        var registry = services.GetRequiredService<SparkBoardRegistry>();
        using var scope = services.CreateScope();
        var kinds = scope.ServiceProvider.GetRequiredService<SparkTileKindRegistry>();
        foreach (var board in registry.Boards.Values)
        foreach (var tile in board.Tiles)
        {
            if (kinds.Find(tile.Kind) is not { } kind)
                throw new InvalidOperationException(
                    $"Board '{board.Id}' tile '{tile.Id}' has kind '{tile.Kind}', which is not registered. Known kinds: {string.Join(", ", kinds.Kinds.Keys)}.");
            if (kind.Validate(tile.Target) is { } error)
                throw new InvalidOperationException($"Board '{board.Id}' tile '{tile.Id}': {error}.");
        }
    }

    private sealed class BoardsMarker;
}
