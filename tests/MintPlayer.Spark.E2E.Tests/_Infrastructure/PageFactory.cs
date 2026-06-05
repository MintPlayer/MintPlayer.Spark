using Microsoft.Playwright;

namespace MintPlayer.Spark.E2E.Tests._Infrastructure;

/// <summary>
/// Per-test helper that spins up a fresh <see cref="IBrowserContext"/> (so cookies/storage
/// are isolated) and yields an <see cref="IPage"/> aimed at Fleet.
/// </summary>
public sealed class PageFactory : IAsyncDisposable
{
    private readonly FleetE2ECollectionFixture _fixture;
    private IBrowserContext? _context;

    public PageFactory(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    public async Task<IPage> NewPageAsync()
    {
        _context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            BaseURL = _fixture.Host.FleetUrl,
        });
        return await _context.NewPageAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_context is not null) await _context.CloseAsync();
    }
}
