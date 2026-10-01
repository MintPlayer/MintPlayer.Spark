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
    /// <param name="colorScheme">
    /// The <c>prefers-color-scheme</c> the browser reports from the first request on, so the
    /// pre-boot script sees it while <c>&lt;head&gt;</c> parses. Null keeps Playwright's default
    /// (<c>light</c>). Change it later in a test with <see cref="IPage.EmulateMediaAsync"/>.
    /// </param>
    /// <remarks>
    /// Every context is tracked and closed on dispose. Calling this more than once per factory is a
    /// supported and deliberate case — two viewers in two zones is exactly why the timezone parameter
    /// exists — and it used to orphan all but the last context.
    /// </remarks>
    public async Task<IPage> NewPageAsync(string? timezoneId = null, ColorScheme? colorScheme = null)
        => await (await NewContextAsync(timezoneId, colorScheme)).NewPageAsync();

    /// <summary>
    /// Opens a fresh, tracked browser context, for tests that need several pages sharing one
    /// cookie jar (two tabs of one user). Same options as <see cref="NewPageAsync"/>.
    /// </summary>
    public async Task<IBrowserContext> NewContextAsync(string? timezoneId = null, ColorScheme? colorScheme = null)
    {
        var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            BaseURL = _fixture.Host.FleetUrl,
            TimezoneId = timezoneId,
            ColorScheme = colorScheme,
        });
        _contexts.Add(context);
        return context;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var context in _contexts) await context.CloseAsync();
        _contexts.Clear();
    }
}
