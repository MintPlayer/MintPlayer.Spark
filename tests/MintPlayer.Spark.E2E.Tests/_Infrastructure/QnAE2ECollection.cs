namespace MintPlayer.Spark.E2E.Tests._Infrastructure;

/// <summary>
/// xUnit collection for the QnA tests: one <see cref="QnATestHost"/> per test session. A collection of
/// its own, so it runs in parallel with Fleet's — the two hosts are separate processes with separate
/// databases, rate-limit buckets and mail folders.
/// </summary>
[CollectionDefinition(Name)]
public class QnAE2ECollection : ICollectionFixture<QnAE2ECollectionFixture>
{
    public const string Name = "QnAE2E";
}

/// <summary>Owns the QnA host and a headless Chromium for the few tests that drive the SPA.</summary>
public sealed class QnAE2ECollectionFixture : IAsyncLifetime
{
    public QnATestHost Host { get; } = new();
    public Microsoft.Playwright.IPlaywright Playwright { get; private set; } = null!;
    public Microsoft.Playwright.IBrowser Browser { get; private set; } = null!;

    public async Task InitializeAsync()
    {
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
