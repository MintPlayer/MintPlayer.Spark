using System.Text.Json.Nodes;

namespace MintPlayer.Spark.E2E.Tests._Infrastructure;

/// <summary>
/// Runs mintplayer.com (<c>apps/MintPlayer</c>) on <see cref="SparkAppTestHost"/>, for the server-rendering tests
/// (<c>docs/spark_ssr_PRD.md</c> M5). Outside Development the app renders with the built server bundle
/// (<c>ClientApp/dist/ClientApp/server</c>), as production does, so these tests run the production path.
/// </summary>
public sealed class MintPlayerTestHost : SparkAppTestHost
{
    public static readonly SparkAppDescriptor MintPlayer = new(
        AppName: "MintPlayer",
        ProjectDirectory: Path.Combine("apps", "MintPlayer", "MintPlayer"),
        ProjectFileName: "MintPlayer.csproj",
        DatabasePrefix: "SparkMintPlayerE2E",
        CoverageSlug: "mintplayer")
    {
        UsesMailPickup = true,
    };

    public MintPlayerTestHost() : base(MintPlayer) { }

    /// <summary>The seeded song and its two artists (<c>Ssr/Data/mintplayer-ssr.json</c>).</summary>
    public const string SongId = "Songs/e2e-sky";
    public const string SongSlug = "a-sky-full-of-stars";

    /// <summary>The home board's second path, served without minification (<c>Spark:Minification:ExcludedPaths</c>).</summary>
    public const string UnminifiedHome = "/home";

    private static readonly string SeedFile = Path.Combine(AppContext.BaseDirectory, "Ssr", "Data", "mintplayer-ssr.json");

    protected override Task ConfigureAppSettings(JsonObject settings, SparkAppHostContext context)
    {
        // Outside Development an account mail is refused without it (#460 M5); the seeded admin registers.
        settings["Spark"]!["Auth"] = new JsonObject { ["PublicBaseUrl"] = context.HttpsUrl };
        // #563: "/" and "/home" render the same board (boards.json names both). Excluding one serves the page twice,
        // minified and as rendered, so MinificationTests can compare what the browser makes of each.
        settings["Spark"]!["Minification"] = new JsonObject { ["ExcludedPaths"] = new JsonArray(UnminifiedHome) };
        // What each page's server render supplied, in the log a failing test prints.
        settings["Logging"] = new JsonObject
        {
            ["LogLevel"] = new JsonObject { ["MintPlayer.Spark.Prerendering"] = "Debug" },
        };
        return Task.CompletedTask;
    }

    protected override async Task SeedAsync()
    {
        await ImportFixturesAsync(SeedFile);
        await WaitForIndexingAsync();
    }
}
