namespace MintPlayer.Spark.E2E.Tests._Infrastructure;

/// <summary>
/// xUnit collection marker — groups every E2E test around a single <see cref="FleetTestHost"/>
/// instance so Fleet only boots once per test session.
/// </summary>
[CollectionDefinition(Name)]
public class FleetE2ECollection : ICollectionFixture<FleetE2ECollectionFixture>
{
    public const string Name = "FleetE2E";
}

/// <summary>
/// Spans the collection's lifetime. Owns both the <see cref="FleetTestHost"/> and the
/// Playwright installation + browser.
/// </summary>
public sealed class FleetE2ECollectionFixture : IAsyncLifetime
{
    public FleetTestHost Host { get; } = new();
    public Microsoft.Playwright.IPlaywright Playwright { get; private set; } = null!;
    public Microsoft.Playwright.IBrowser Browser { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // Install Chromium the first time we run. No-op if already installed.
        var exitCode = Microsoft.Playwright.Program.Main(["install", "chromium", "--with-deps"]);
        if (exitCode != 0)
            throw new InvalidOperationException($"Playwright install failed with exit code {exitCode}");

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
