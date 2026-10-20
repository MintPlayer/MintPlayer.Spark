using System.Globalization;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.DateTimes;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// The server twin of <c>&lt;spark-date-time&gt;</c> (commerce PRD X16, D35): the recipient's zone, a hint
/// hidden when the wall clocks match at that instant, the stored wall clock with its offset, and zone labels.
/// Expected texts are built with the same culture patterns, so ICU's spacing never decides a result.
/// </summary>
public class SparkDateTimeFormatterTests
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");
    private static readonly SparkRecipientLocale Brussels = new("Europe/Brussels", "en-US");

    private static SparkDateTimeFormatter Formatter(ISparkTimeZoneNameProvider? provider = null)
        => new(new SparkTimeZoneNames(provider, null));

    private static string G(int y, int mo, int d, int h, int mi) => new DateTime(y, mo, d, h, mi, 0).ToString("g", En);

    [Fact]
    public async Task Viewer_means_the_recipients_zone_on_the_values_own_date()
    {
        // 08:00Z is 09:00 in Brussels on 9 March (CET), whatever the offset in force today.
        var text = await Formatter().FormatAsync(new DateTimeOffset(2026, 3, 9, 8, 0, 0, TimeSpan.Zero), Brussels);
        text.Should().Be(G(2026, 3, 9, 9, 0));
    }

    [Fact]
    public async Task A_hint_in_another_zone_is_added_with_its_city()
    {
        var text = await Formatter().FormatAsync(new DateTimeOffset(2026, 7, 4, 19, 0, 0, TimeSpan.Zero), Brussels,
            new SparkDateTimeFormatOptions { Hint = SparkDateTimeZone.InZone("Europe/London") });
        text.Should().Be($"{G(2026, 7, 4, 21, 0)} ({G(2026, 7, 4, 20, 0)} London)");
    }

    [Fact]
    public async Task The_hint_is_hidden_when_both_wall_clocks_match_at_that_instant()
    {
        // Compared by wall clock, never by zone name: Paris and Brussels agree.
        var text = await Formatter().FormatAsync(new DateTimeOffset(2026, 7, 4, 19, 0, 0, TimeSpan.Zero), Brussels,
            new SparkDateTimeFormatOptions { Hint = SparkDateTimeZone.InZone("Europe/Paris") });
        text.Should().Be(G(2026, 7, 4, 21, 0));
    }

    [Fact]
    public async Task Stored_shows_the_wall_clock_the_value_was_written_with_and_its_offset()
    {
        var text = await Formatter().FormatAsync(new DateTimeOffset(2026, 3, 9, 10, 0, 0, TimeSpan.FromHours(2)), Brussels,
            new SparkDateTimeFormatOptions { Primary = SparkDateTimeZone.Stored });
        text.Should().Be($"{G(2026, 3, 9, 10, 0)} UTC+02:00");
    }

    [Theory]
    [InlineData(0, "UTC")]
    [InlineData(120, "UTC+02:00")]
    [InlineData(-210, "UTC-03:30")]
    public void Offset_labels_match_the_component(int minutes, string expected)
        => SparkDateTimeFormatter.OffsetLabel(TimeSpan.FromMinutes(minutes)).Should().Be(expected);

    [Fact]
    public async Task ZoneFrom_reads_the_zone_from_the_object_and_is_left_out_when_it_has_none()
    {
        var value = new DateTimeOffset(2026, 7, 4, 19, 0, 0, TimeSpan.Zero);
        var display = new SparkDateTimeDisplay { Hint = SparkDateTimeZone.FromAttribute("VenueTimeZoneId") };

        var withVenue = await Formatter().FormatAsync(value, Brussels,
            SparkDateTimeFormatOptions.From(display, name => name == "VenueTimeZoneId" ? "Asia/Tokyo" : null));
        withVenue.Should().Be($"{G(2026, 7, 4, 21, 0)} ({G(2026, 7, 5, 4, 0)} Tokyo)");

        var withoutVenue = await Formatter().FormatAsync(value, Brussels, SparkDateTimeFormatOptions.From(display, _ => null));
        withoutVenue.Should().Be(G(2026, 7, 4, 21, 0));
    }

    [Fact]
    public async Task A_fixed_main_zone_carries_its_label()
    {
        var text = await Formatter().FormatAsync(new DateTimeOffset(2026, 7, 4, 19, 0, 0, TimeSpan.Zero), Brussels,
            new SparkDateTimeFormatOptions { Primary = SparkDateTimeZone.InZone("America/New_York") });
        text.Should().Be($"{G(2026, 7, 4, 15, 0)} New York");
    }

    [Fact]
    public async Task The_label_comes_from_the_name_provider_when_one_is_registered()
    {
        var provider = Substitute.For<ISparkTimeZoneNameProvider>();
        provider.GetCityNameAsync("Europe/London", "nl-be", Arg.Any<CancellationToken>()).Returns(new ValueTask<string?>("Londen"));
        var nl = CultureInfo.GetCultureInfo("nl-BE");

        var text = await Formatter(provider).FormatAsync(new DateTimeOffset(2026, 7, 4, 19, 0, 0, TimeSpan.Zero),
            new SparkRecipientLocale("Europe/Brussels", "nl-BE"),
            new SparkDateTimeFormatOptions { Hint = SparkDateTimeZone.InZone("Europe/London") });

        text.Should().Be($"{new DateTime(2026, 7, 4, 21, 0, 0).ToString("g", nl)} ({new DateTime(2026, 7, 4, 20, 0, 0).ToString("g", nl)} Londen)");
    }

    [Fact]
    public async Task An_unknown_recipient_zone_or_culture_reads_as_UTC_and_invariant()
    {
        var text = await Formatter().FormatAsync(new DateTimeOffset(2026, 3, 9, 8, 0, 0, TimeSpan.Zero),
            new SparkRecipientLocale("Not/AZone", "xx-not-a-culture"));
        text.Should().Be(new DateTime(2026, 3, 9, 8, 0, 0).ToString("g", CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Styles_pick_the_culture_patterns()
    {
        var value = new DateTimeOffset(2026, 7, 4, 7, 0, 0, TimeSpan.Zero);
        var utc = new SparkRecipientLocale("UTC", "en-US");
        var at = new DateTime(2026, 7, 4, 7, 0, 0);

        (await Formatter().FormatAsync(value, utc, new SparkDateTimeFormatOptions { Style = ESparkDateTimeStyle.LongDateTime })).Should().Be(at.ToString("f", En));
        (await Formatter().FormatAsync(value, utc, new SparkDateTimeFormatOptions { Style = ESparkDateTimeStyle.Date })).Should().Be(at.ToString("d", En));
        (await Formatter().FormatAsync(value, utc, new SparkDateTimeFormatOptions { Style = ESparkDateTimeStyle.Time })).Should().Be(at.ToString("t", En));
    }
}
