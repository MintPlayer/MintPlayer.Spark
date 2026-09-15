using System.Globalization;
using Microsoft.Playwright;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Client.Authorization;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Mapper;

/// <summary>
/// Two users in two timezones look at the same record, on the query page and the detail page.
/// </summary>
/// <remarks>
/// <para>
/// This is the layer nothing else reaches. The HTTP tests assert what is on the wire; the ng-spark
/// specs assert the conversion functions in isolation. Neither can answer the question a user
/// actually asks — <i>does the person in Kathmandu see the right time?</i> — because the conversion
/// happens in the browser, from the browser's own timezone, in a real render.
/// </para>
/// <para>
/// The invariant has two halves, and the second is what makes the first meaningful: each viewer sees
/// the instant in <b>their own</b> zone, and the <b>query page and detail page agree with each
/// other</b>. That pairing is the defect this whole change set began with — a grid answered from the
/// index and a detail page answered from the document, naming different days for one car.
/// </para>
/// <para>
/// ⚠️ <b>Kept to one test on purpose.</b> Fleet's rate limiter is a fixed window of 150 requests per
/// 10 seconds partitioned by client IP, and every test in this collection shares <c>127.0.0.1</c>.
/// A browser test is expensive in that budget — one Angular boot is a dozen <c>/spark</c> calls — so
/// a second near-duplicate test pushed unrelated tests elsewhere in the suite into 429s. For the same
/// reason the detail page is reached by <b>clicking through</b> rather than a second navigation:
/// it is one SPA boot instead of two, and it is also what a user does.
/// </para>
/// </remarks>
[Collection(FleetE2ECollection.Name)]
public class ViewerTimezoneRenderingTests
{
    // ⚠️ Los Angeles is PDT (-07:00) in June, while the value below is recorded at -08:00. That
    // mismatch is deliberate: a viewer must see the instant under their zone's rules FOR THAT DATE,
    // not the offset the value happens to carry. A client that echoed the stored offset would be an
    // hour out, and only this would show it.
    private const string Seattle = "America/Los_Angeles";
    private const string Kathmandu = "Asia/Kathmandu";      // +05:45 — one of the few :45 zones

    private readonly FleetE2ECollectionFixture _fixture;
    public ViewerTimezoneRenderingTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Two_users_in_two_timezones_each_see_the_instant_in_their_own_zone_on_both_pages()
    {
        // A value that crosses a date boundary once converted, so the two users legitimately see
        // different DAYS for one car -- the failure mode that started this work.
        var registeredAt = new DateTimeOffset(2026, 6, 15, 23, 30, 0, TimeSpan.FromHours(-8));

        // ⚠️ The second user needs the Administrators ROLE, not just the group claim. Fleet's
        // CarActions filters rows to those the caller created unless CurrentUser.IsInRole
        // ("Administrators"), so a user holding only group=Administrators sees an empty grid — and
        // this test would then fail on a missing row, which looks nothing like a permissions problem.
        var secondEmail = $"tz-{Guid.NewGuid():N}@e2e.local";
        var secondPassword = _fixture.Host.AdminPass;
        await _fixture.Host.SeedUserAsync(secondEmail, secondPassword, "Administrators", roleName: "Administrators");

        var plate = CarFixture.RandomLicensePlate("TZ");
        using var client = await SparkClientFactory.ForFleetAsAdminAsync(_fixture.Host);
        var created = await client.CreatePersistentObjectAsync(CarFixture.New(plate, registeredAt: registeredAt));
        created.Id.Should().NotBeNullOrEmpty(
            $"car create must succeed\n--- Fleet log tail ---\n{_fixture.Host.RecentLog()}");

        // The query page reads an index, and indexes are eventually consistent.
        await _fixture.Host.WaitForIndexingAsync();

        await using var pages = new PageFactory(_fixture);

        var author = await ReadBothPagesAsync(
            pages, Seattle, plate, registeredAt, _fixture.Host.AdminEmailAddress, _fixture.Host.AdminPass);
        var reader = await ReadBothPagesAsync(
            pages, Kathmandu, plate, registeredAt, secondEmail, secondPassword);

        // 1. Within one viewer, the two pages must agree EXACTLY. Both run the same default path --
        //    new Date(wire) then | date:'short' -- so this is byte equality, not "same moment".
        author.Grid.Should().Be(author.Detail,
            $"query page and detail page must render one record identically for one viewer ({Seattle})");
        reader.Grid.Should().Be(reader.Detail,
            $"query page and detail page must render one record identically for one viewer ({Kathmandu})");

        // 2. Between viewers they must DIFFER -- otherwise the assertions above would also pass
        //    against a server-rendered constant, and this would prove nothing about timezones.
        author.Grid.Should().NotBe(reader.Grid,
            "two viewers 13h45m apart must not see the same wall clock; if they do, the value is not "
            + "being converted in the browser at all");

        // 3. And they must differ by the RIGHT amount: each must show the clock time the original
        //    instant has in that viewer's zone.
        AssertRendersInstant(author.Grid, registeredAt, Seattle);
        AssertRendersInstant(reader.Grid, registeredAt, Kathmandu);
    }

    // ---------------------------------------------------------------------------------

    private sealed record Rendered(string Grid, string Detail);

    /// <summary>
    /// Signs in as the given user, reads RegisteredAt from the query page, clicks through to the
    /// detail page and reads it there — in a browser that believes it is in
    /// <paramref name="timezoneId"/>.
    /// </summary>
    private async Task<Rendered> ReadBothPagesAsync(
        PageFactory pages,
        string timezoneId,
        string plate,
        DateTimeOffset probeInstant,
        string email,
        string password)
    {
        var page = await pages.NewPageAsync(timezoneId);
        await BrowserSignIn.SignInAsync(page, _fixture.Host.FleetUrl, email, password);

        await page.GotoAsync("/query/registrations");
        await AssertBrowserIsInZoneAsync(page, timezoneId, probeInstant);

        // Search narrows the grid to the one row, which also avoids paging entirely.
        var search = page.Locator("input[type='search'], input[placeholder*='Search']").First;
        await search.WaitForAsync(new() { Timeout = 15_000 });
        await search.FillAsync(plate);

        var row = page.Locator("tbody tr").Filter(new() { HasTextString = plate }).First;
        await row.WaitForAsync(new() { Timeout = 15_000 });

        // The column is found by matching its header text rather than a fixed index: Car's query
        // carries a dozen columns and their order is a model-file detail that has already moved once.
        var headers = await page.Locator("thead th").AllInnerTextsAsync();
        var index = headers.ToList().FindIndex(h => h.Contains("Registered At", StringComparison.Ordinal));
        index.Should().BeGreaterThanOrEqualTo(0,
            $"the Registered At column must be present; headers were [{string.Join(" | ", headers)}]");

        var grid = (await row.Locator("td").Nth(index).InnerTextAsync()).Trim();

        // Click through the way a user does. Also one SPA boot rather than two -- see the class note
        // on the shared rate-limit budget.
        await row.Locator("a").First.ClickAsync();

        var detailValue = page.Locator("dt:has-text('Registered At') + dd").First;
        await detailValue.WaitForAsync(new() { Timeout = 15_000 });
        var detail = (await detailValue.InnerTextAsync()).Trim();

        return new Rendered(grid, detail);
    }

    /// <summary>
    /// Asserts the browser really is in <paramref name="timezoneId"/>, by comparing the offset it
    /// applies at <paramref name="probeInstant"/> against what .NET says that zone's offset is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this guard, a <c>TimezoneId</c> option that silently stopped applying would leave both
    /// viewers in the machine's own zone: the two pages would still agree, the strings would still be
    /// equal, and the test would pass while measuring nothing.
    /// </para>
    /// <para>
    /// ⚠️ Compared by <b>offset</b> rather than by zone id, because the ids legitimately disagree.
    /// Chromium's ICU reports the deprecated <c>Asia/Katmandu</c> for the canonical
    /// <c>Asia/Kathmandu</c> — the same class of rename as <c>America/Godthab</c> → <c>America/Nuuk</c>
    /// and <c>Asia/Calcutta</c> vs <c>Asia/Kolkata</c>. An id comparison here fails on a spelling,
    /// which is exactly the trap <c>RequestTimeZoneResolver</c> absorbs on the server side.
    /// </para>
    /// </remarks>
    private static async Task AssertBrowserIsInZoneAsync(IPage page, string timezoneId, DateTimeOffset probeInstant)
    {
        var iso = probeInstant.ToString("o", CultureInfo.InvariantCulture);

        // getTimezoneOffset() is minutes to ADD to local to reach UTC, so negate it to get the offset
        // an ISO string would carry.
        var browserOffsetMinutes = await page.EvaluateAsync<int>(
            "(iso) => -new Date(iso).getTimezoneOffset()", iso);

        var expected = TimeZoneInfo.FindSystemTimeZoneById(timezoneId).GetUtcOffset(probeInstant);

        ((int)expected.TotalMinutes).Should().Be(browserOffsetMinutes,
            $"the browser context must actually be in {timezoneId} at {iso}; it reported "
            + $"{browserOffsetMinutes} minutes and .NET expects {expected.TotalMinutes}");
    }

    /// <summary>
    /// Asserts that <paramref name="rendered"/> shows the clock time <paramref name="expected"/> has
    /// when read in <paramref name="timezoneId"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately <b>not</b> parsed back into a <see cref="DateTime"/>. Angular's <c>'short'</c>
    /// format is locale-dependent — day-first or month-first, 12-hour or 24-hour — so a parse with any
    /// fixed culture is a coin flip that would fail on a date like the 31st for reasons unrelated to
    /// timezones. Asserting the clock time is locale-proof, and it is the half a timezone bug moves.
    /// Kathmandu's <c>+05:45</c> lands the expected value on a quarter-hour no whole-hour error can
    /// produce.
    /// </remarks>
    private static void AssertRendersInstant(string rendered, DateTimeOffset expected, string timezoneId)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timezoneId);
        var local = TimeZoneInfo.ConvertTime(expected, zone);

        var hour24 = local.Hour;
        var hour12 = hour24 % 12 == 0 ? 12 : hour24 % 12;
        var candidates = new[]
        {
            $"{hour24:00}:{local.Minute:00}",
            $"{hour24}:{local.Minute:00}",
            $"{hour12}:{local.Minute:00}",
            $"{hour12:00}:{local.Minute:00}",
        };

        candidates.Any(c => rendered.Contains(c, StringComparison.Ordinal)).Should().BeTrue(
            $"'{rendered}' must show {expected:o} as seen from {timezoneId} — expected clock time "
            + $"{hour24:00}:{local.Minute:00}, one of [{string.Join(", ", candidates)}]");
    }
}
