namespace MintPlayer.Spark.E2E.Tests._Infrastructure;

/// <summary>
/// xUnit collection for the SqlDemo tests: one <see cref="SqlDemoTestHost"/> per test session, in parallel with the
/// other apps' (separate processes, databases, rate-limit buckets and mail folders).
/// </summary>
[CollectionDefinition(Name)]
public class SqlDemoE2ECollection : ICollectionFixture<SqlDemoE2ECollectionFixture>
{
    public const string Name = "SqlDemoE2E";
}

/// <summary>Owns the SqlDemo host and a headless Chromium.</summary>
public sealed class SqlDemoE2ECollectionFixture : IAsyncLifetime
{
    public SqlDemoTestHost Host { get; } = new();
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
