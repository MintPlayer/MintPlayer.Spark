namespace MintPlayer.Spark.E2E.Tests._Infrastructure;

/// <summary>
/// xUnit collection marker for the AI-agent journeys: one Fleet host that is also its agents' authorization server
/// (<see cref="FleetTestHost.AgentAuthorizationServer"/>), and a browser.
/// </summary>
[CollectionDefinition(Name)]
public class FleetMcpE2ECollection : ICollectionFixture<FleetMcpE2EFixture>
{
    public const string Name = "FleetMcpE2E";
}

/// <summary>
/// A Fleet host of its own: the provider adds <c>/connect/*</c> and the OAuth metadata, which the shared host must not
/// have (its <c>/mcp</c> takes personal access tokens only), and a separate process keeps these journeys out of the
/// shared host's rate-limit bucket.
/// </summary>
public sealed class FleetMcpE2EFixture : IAsyncLifetime
{
    // A distinct environment name: every Fleet host writes appsettings.{Environment}.json into the Fleet project
    // directory and deletes it on dispose.
    public FleetTestHost Host { get; } = new() { EnvironmentName = "E2EMcp", AgentAuthorizationServer = true };
    public Microsoft.Playwright.IPlaywright Playwright { get; private set; } = null!;
    public Microsoft.Playwright.IBrowser Browser { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await PlaywrightInstall.EnsureAsync();
        await Host.InitializeAsync();
        Playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        Browser = await Playwright.Chromium.LaunchAsync(new() { Headless = true });
    }

    public async Task DisposeAsync()
    {
        if (Browser is not null) await Browser.CloseAsync();
        Playwright?.Dispose();
        await Host.DisposeAsync();
    }
}
