using Microsoft.Playwright;

namespace MintPlayer.Spark.E2E.Tests._Infrastructure;

/// <summary>
/// Per-test helper that spins up a fresh <see cref="IBrowserContext"/> (so cookies/storage
/// are isolated) and yields an <see cref="IPage"/> aimed at Fleet.
/// </summary>
public sealed class PageFactory : IAsyncDisposable
{
    private readonly FleetE2ECollectionFixture _fixture;
    private readonly List<IBrowserContext> _contexts = [];

    public PageFactory(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Opens a page in a fresh browser context.
    /// </summary>
    /// <param name="timezoneId">
    /// IANA zone the browser should believe it is in, e.g. <c>America/Los_Angeles</c>. Drives both
    /// what Angular's <c>DatePipe</c> renders and what the <c>X-Spark-Timezone</c> header carries, so
    /// it is the lever for testing that a viewer sees timestamps in their OWN zone. Null keeps the
    /// machine's zone, which is what every pre-existing test wants.
    /// </param>
    /// <remarks>
    /// Every context is tracked and closed on dispose. Calling this more than once per factory is a
    /// supported and deliberate case — two viewers in two zones is exactly why the timezone parameter
    /// exists — and it used to orphan all but the last context.
    /// </remarks>
    public async Task<IPage> NewPageAsync(string? timezoneId = null)
    {
        var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            BaseURL = _fixture.Host.FleetUrl,
            TimezoneId = timezoneId,
        });
        _contexts.Add(context);
        return await context.NewPageAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var context in _contexts) await context.CloseAsync();
        _contexts.Clear();
    }
}
