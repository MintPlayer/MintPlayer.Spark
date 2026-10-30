namespace MintPlayer.Spark.E2E.Tests._Infrastructure;

/// <summary>
/// xUnit collection for the MintPlayer tests: one <see cref="MintPlayerTestHost"/> per test session, in parallel
/// with the other apps' (separate processes, databases, rate-limit buckets and mail folders).
/// </summary>
[CollectionDefinition(Name)]
public class MintPlayerE2ECollection : ICollectionFixture<MintPlayerE2ECollectionFixture>
{
    public const string Name = "MintPlayerE2E";
}

/// <summary>Owns the MintPlayer host and a headless Chromium.</summary>
public sealed class MintPlayerE2ECollectionFixture : IAsyncLifetime
{
    public MintPlayerTestHost Host { get; } = new();
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
