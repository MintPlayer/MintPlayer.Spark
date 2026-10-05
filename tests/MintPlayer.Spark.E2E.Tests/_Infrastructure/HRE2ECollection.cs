namespace MintPlayer.Spark.E2E.Tests._Infrastructure;

/// <summary>
/// xUnit collection for the HR tests: one <see cref="HRTestHost"/> per test session, in parallel with Fleet's
/// and QnA's (separate processes, databases, rate-limit buckets and mail folders).
/// </summary>
[CollectionDefinition(Name)]
public class HRE2ECollection : ICollectionFixture<HRE2ECollectionFixture>
{
    public const string Name = "HRE2E";
}

/// <summary>Owns the HR host and a headless Chromium.</summary>
public sealed class HRE2ECollectionFixture : IAsyncLifetime
{
    public HRTestHost Host { get; } = new();
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
