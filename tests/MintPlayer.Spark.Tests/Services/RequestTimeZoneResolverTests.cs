using Microsoft.AspNetCore.Http;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Pins the DST policy chosen from the M15 spike (2026-09-12).
/// <para>
/// The measurement that motivates all of this: at the autumn fold, .NET's <c>GetUtcOffset</c> picks
/// the <b>standard</b> offset while a browser picks the <b>daylight</b> one, so the same wall clock
/// lands an hour apart depending on which side converts it. The resolver deliberately matches the
/// browser, and these tests are what stop that quietly reverting to the default.
/// </para>
/// </summary>
public class RequestTimeZoneResolverTests
{
    private readonly IHttpContextAccessor _httpContextAccessor = Substitute.For<IHttpContextAccessor>();

    private RequestTimeZoneResolver CreateResolver(string? header)
    {
        if (header is null)
        {
            _httpContextAccessor.HttpContext.Returns((HttpContext?)null);
        }
        else
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Headers[RequestTimeZoneResolver.HeaderName] = header;
            _httpContextAccessor.HttpContext.Returns(ctx);
        }

        return new RequestTimeZoneResolver(_httpContextAccessor, null);
    }

    private static TimeZoneInfo Brussels => TimeZoneInfo.FindSystemTimeZoneById("Europe/Brussels");

    [Fact]
    public void Resolves_an_IANA_zone_id_from_the_header()
    {
        var resolver = CreateResolver("Europe/Brussels");
        resolver.GetViewerTimeZone().Id.Should().Be(Brussels.Id);
    }

    [Fact]
    public void Falls_back_to_UTC_when_the_header_is_absent()
    {
        CreateResolver(null).GetViewerTimeZone().Should().Be(TimeZoneInfo.Utc);
        CreateResolver("").GetViewerTimeZone().Should().Be(TimeZoneInfo.Utc);
        CreateResolver("   ").GetViewerTimeZone().Should().Be(TimeZoneInfo.Utc);
    }

    [Fact]
    public void Falls_back_to_UTC_for_an_unknown_zone_rather_than_throwing()
    {
        // The realistic cause is a zone RENAME between the browser's tzdata and the server's, not a
        // malicious value -- but either way a request must not fail over it.
        CreateResolver("Mars/Olympus_Mons").GetViewerTimeZone().Should().Be(TimeZoneInfo.Utc);
        CreateResolver("../../etc/passwd").GetViewerTimeZone().Should().Be(TimeZoneInfo.Utc);
    }

    [Fact]
    public void An_ordinary_wall_clock_converts_with_the_offset_for_that_date()
    {
        var resolver = CreateResolver("Europe/Brussels");

        // Not a fixed shift: the same clock time is +01:00 in winter and +02:00 in summer.
        resolver.ToViewerDateTimeOffset(new DateTime(2026, 1, 15, 12, 0, 0)).Offset
            .Should().Be(TimeSpan.FromHours(1));
        resolver.ToViewerDateTimeOffset(new DateTime(2026, 7, 15, 12, 0, 0)).Offset
            .Should().Be(TimeSpan.FromHours(2));
    }

    [Fact]
    public void At_the_autumn_fold_it_picks_the_daylight_offset_to_match_the_browser()
    {
        // 2026-10-25T02:30 in Brussels happens twice: once at +02:00 and again at +01:00.
        // GetUtcOffset alone returns +01:00; a browser returns +02:00. Measured across
        // Europe/Brussels, Australia/Sydney, America/Santiago and America/New_York.
        var wall = new DateTime(2026, 10, 25, 2, 30, 0);
        Brussels.IsAmbiguousTime(wall).Should().BeTrue("the fixture must actually sit inside the fold");
        Brussels.GetUtcOffset(wall).Should().Be(TimeSpan.FromHours(1), "this is the default we are overriding");

        var resolved = CreateResolver("Europe/Brussels").ToViewerDateTimeOffset(wall);

        resolved.Offset.Should().Be(TimeSpan.FromHours(2));
        resolved.UtcDateTime.Should().Be(new DateTime(2026, 10, 25, 0, 30, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void At_the_spring_gap_it_resolves_without_throwing()
    {
        // 2026-03-29T02:30 never happens. ConvertTimeToUtc THROWS on it, which is why the resolver
        // does not use it -- this value can come straight from something a person typed.
        var wall = new DateTime(2026, 3, 29, 2, 30, 0);
        Brussels.IsInvalidTime(wall).Should().BeTrue("the fixture must actually sit inside the gap");
        var convert = () => TimeZoneInfo.ConvertTimeToUtc(wall, Brussels);
        convert.Should().Throw<ArgumentException>();

        var resolved = CreateResolver("Europe/Brussels").ToViewerDateTimeOffset(wall);

        // Measured: the browser shifts the clock forward to 03:30+02:00, which is this same instant.
        // Only the offset label differs, and the instant is what Spark stores.
        resolved.UtcDateTime.Should().Be(new DateTime(2026, 3, 29, 1, 30, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void A_DateTime_that_already_carries_a_kind_is_not_reinterpreted()
    {
        var resolver = CreateResolver("Europe/Brussels");
        var utc = new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);

        var resolved = resolver.ToViewerDateTimeOffset(utc);

        resolved.Offset.Should().Be(TimeSpan.Zero);
        resolved.UtcDateTime.Should().Be(utc);
    }

    [Fact]
    public void An_explicit_zone_overrides_the_header()
    {
        var resolver = CreateResolver("Europe/Brussels");
        var tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

        resolver.ToViewerDateTimeOffset(new DateTime(2026, 7, 15, 12, 0, 0), tokyo).Offset
            .Should().Be(TimeSpan.FromHours(9));
    }

    [Theory]
    [InlineData("Australia/Sydney", 2026, 4, 5, 11)]
    [InlineData("America/New_York", 2026, 11, 1, -4)]
    public void The_fold_rule_holds_in_other_zones_including_the_southern_hemisphere(
        string zoneId, int year, int month, int day, int expectedHours)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        // Sydney folds at 02:30 on 5 April; New York at 01:30 on 1 November.
        var wall = new DateTime(year, month, day, zoneId == "America/New_York" ? 1 : 2, 30, 0);
        zone.IsAmbiguousTime(wall).Should().BeTrue();

        var resolved = CreateResolver(zoneId).ToViewerDateTimeOffset(wall, zone);

        // The daylight offset in both cases -- the larger one, which is what the browser picks.
        resolved.Offset.Should().Be(TimeSpan.FromHours(expectedHours));
        resolved.Offset.Should().Be(zone.GetAmbiguousTimeOffsets(wall).Max());
    }
}
