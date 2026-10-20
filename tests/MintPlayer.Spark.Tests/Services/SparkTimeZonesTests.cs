using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.DateTimes;
using MintPlayer.Spark.Configuration;
using MintPlayer.Spark.Services;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>The core lookup and conversion the K10 ban points every library and app to.</summary>
public class SparkTimeZonesTests
{
    [Theory]
    [InlineData("Europe/Brussels", "Brussels")]
    [InlineData("America/Argentina/Buenos_Aires", "Buenos Aires")]
    [InlineData("UTC", "UTC")]
    [InlineData("", "UTC")]
    public void CityFromId_derives_the_English_city(string id, string expected)
        => SparkTimeZones.CityFromId(id).Should().Be(expected);

    [Fact]
    public void Find_validates_the_shape_before_any_lookup_and_answers_null_for_the_unknown()
    {
        SparkTimeZones.Find("Europe/Brussels").Should().NotBeNull();
        SparkTimeZones.Find("Romance Standard Time").Should().BeNull("a Windows id is refused, like the header");
        SparkTimeZones.Find("Europe//Brussels").Should().BeNull();
        SparkTimeZones.Find("Not/AZone").Should().BeNull();
        SparkTimeZones.FindOrUtc(null).Should().Be(TimeZoneInfo.Utc);
        SparkTimeZones.FindOrUtc("Not/AZone").Should().Be(TimeZoneInfo.Utc);
        SparkTimeZones.FindOrUtc("UTC").Should().Be(TimeZoneInfo.Utc);
    }

    [Fact]
    public void ToInstant_takes_the_browsers_offset_in_the_autumn_fold_and_never_throws_in_the_spring_gap()
    {
        var brussels = SparkTimeZones.FindOrUtc("Europe/Brussels");

        SparkTimeZones.ToInstant(new DateTime(2026, 10, 25, 2, 30, 0), brussels).Offset.Should().Be(TimeSpan.FromHours(2));
        var gap = SparkTimeZones.ToInstant(new DateTime(2026, 3, 29, 2, 30, 0), brussels);
        gap.UtcDateTime.Should().Be(new DateTime(2026, 3, 29, 1, 30, 0));
    }

    [Fact]
    public void ToZone_keeps_the_instant()
    {
        var instant = new DateTimeOffset(2026, 7, 4, 19, 0, 0, TimeSpan.Zero);
        var tokyo = SparkTimeZones.ToZone(instant, SparkTimeZones.FindOrUtc("Asia/Tokyo"));
        tokyo.Offset.Should().Be(TimeSpan.FromHours(9));
        tokyo.UtcDateTime.Should().Be(instant.UtcDateTime);
    }

    [Theory]
    [InlineData("Europe/Brussels", true)]
    [InlineData("Etc/GMT+5", true)]
    [InlineData("Romance Standard Time", false)]
    [InlineData("../etc/passwd", false)]
    [InlineData("", false)]
    public void SparkTimeZoneIds_is_the_shared_shape_check(string id, bool expected)
        => SparkTimeZoneIds.IsWellFormed(id).Should().Be(expected);
}

/// <summary>D36 on the server: provider, then the English city (no platform step, S23), cached per (zone, language).</summary>
public class SparkTimeZoneNamesTests
{
    [Fact]
    public async Task Without_a_provider_the_label_is_the_English_city()
        => (await new SparkTimeZoneNames(null, null).GetLabelAsync("Europe/Brussels", "nl")).Should().Be("Brussels");

    [Fact]
    public async Task The_provider_wins_and_is_asked_once_per_zone_and_language()
    {
        var provider = Substitute.For<ISparkTimeZoneNameProvider>();
        provider.GetCityNameAsync("Europe/Brussels", "nl", Arg.Any<CancellationToken>()).Returns(new ValueTask<string?>("Brussel"));
        var names = new SparkTimeZoneNames(provider, null);

        (await names.GetLabelAsync("Europe/Brussels", "nl")).Should().Be("Brussel");
        (await names.GetLabelAsync("Europe/Brussels", "NL")).Should().Be("Brussel");

        await provider.Received(1).GetCityNameAsync("Europe/Brussels", "nl", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_provider_without_the_zone_falls_back_to_the_city()
    {
        var provider = Substitute.For<ISparkTimeZoneNameProvider>();
        provider.GetCityNameAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<string?>((string?)null));

        (await new SparkTimeZoneNames(provider, null).GetLabelAsync("America/New_York", "fr")).Should().Be("New York");
    }

    [Fact]
    public async Task A_failing_provider_falls_back_and_is_asked_again_next_time()
    {
        var provider = Substitute.For<ISparkTimeZoneNameProvider>();
        provider.GetCityNameAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Throws(new HttpRequestException("down"));
        var names = new SparkTimeZoneNames(provider, null);

        (await names.GetLabelAsync("Europe/Brussels", "nl")).Should().Be("Brussels");
        (await names.GetLabelAsync("Europe/Brussels", "nl")).Should().Be("Brussels");

        await provider.Received(2).GetCityNameAsync("Europe/Brussels", "nl", Arg.Any<CancellationToken>());
    }
}

/// <summary>The model-file form of <c>dateTimeDisplay</c> (X16): read case-insensitively, written back unchanged.</summary>
public class SparkDateTimeDisplayJsonTests
{
    private static readonly JsonSerializerOptions Read = new() { PropertyNameCaseInsensitive = true };
    // The synchronizer's own options (ModelSynchronizer.JsonOptions), minus indentation.
    private static readonly JsonSerializerOptions Write = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    [Theory]
    [InlineData("""{"primary":"viewer","hint":{"zoneFrom":"VenueTimeZoneId"}}""")]
    [InlineData("""{"primary":"stored"}""")]
    [InlineData("""{"primary":{"zone":"Europe/Brussels"},"hint":"viewer"}""")]
    public void Round_trips_as_a_synchronize_fixed_point(string json)
    {
        var display = JsonSerializer.Deserialize<SparkDateTimeDisplay>(json, Read)!;
        JsonSerializer.Serialize(display, Write).Should().Be(json);
    }

    [Fact]
    public void Reads_the_kinds()
    {
        var display = JsonSerializer.Deserialize<SparkDateTimeDisplay>("""{"Primary":{"Zone":"Asia/Tokyo"},"Hint":{"ZoneFrom":"Venue"}}""", Read)!;
        display.Primary!.Kind.Should().Be(ESparkDateTimeZoneKind.Zone);
        display.Primary.Zone.Should().Be("Asia/Tokyo");
        display.Hint!.Kind.Should().Be(ESparkDateTimeZoneKind.ZoneFrom);
        display.Hint.ZoneFrom.Should().Be("Venue");
    }

    [Theory]
    [InlineData("""{"primary":"local"}""")]
    [InlineData("""{"primary":{"zone":"Europe/Brussels","zoneFrom":"X"}}""")]
    [InlineData("""{"primary":{}}""")]
    public void Refuses_a_malformed_rule_with_a_message_naming_it(string json)
    {
        var act = () => JsonSerializer.Deserialize<SparkDateTimeDisplay>(json, Read);
        act.Should().Throw<JsonException>().WithMessage("*dateTimeDisplay:*");
    }
}

/// <summary>X14: the locale server-rendered output is written in.</summary>
public class SparkRecipientLocaleResolverTests
{
    private readonly IRequestTimeZoneResolver timeZones = Substitute.For<IRequestTimeZoneResolver>();
    private readonly IRequestCultureResolver cultures = Substitute.For<IRequestCultureResolver>();
    private readonly ICultureLoader cultureLoader = Substitute.For<ICultureLoader>();
    private readonly IHttpContextAccessor http = Substitute.For<IHttpContextAccessor>();
    private readonly ISparkRecipientLocaleSource source = Substitute.For<ISparkRecipientLocaleSource>();

    private SparkRecipientLocaleResolver Create(bool inRequest, ISparkRecipientLocaleSource? withSource, string? defaultZone = "UTC")
    {
        cultureLoader.GetCulture().Returns(new CultureConfiguration { DefaultLanguage = "nl" });
        http.HttpContext.Returns(inRequest ? new DefaultHttpContext() : (HttpContext?)null);
        timeZones.GetViewerTimeZone().Returns(SparkTimeZones.FindOrUtc("America/New_York"));
        cultures.GetCurrentCulture().Returns("fr");
        return new SparkRecipientLocaleResolver(timeZones, cultures, cultureLoader, http,
            Options.Create(new SparkTimeZoneOptions { DefaultRecipientTimeZone = defaultZone }), withSource);
    }

    [Fact]
    public async Task Without_accounts_every_user_gets_the_defaults()
        => (await Create(false, null, "Europe/Brussels").ForUserAsync("users/1")).Should().Be(new SparkRecipientLocale("Europe/Brussels", "nl"));

    [Fact]
    public async Task The_stored_preference_wins_part_by_part_and_never_the_request()
    {
        source.GetAsync("users/1", Arg.Any<CancellationToken>()).Returns(Task.FromResult<SparkRecipientLocalePreference?>(new SparkRecipientLocalePreference("Asia/Tokyo", null)));

        var locale = await Create(true, source).ForUserAsync("users/1");

        locale.Should().Be(new SparkRecipientLocale("Asia/Tokyo", "nl"), "a job one person triggers must not use their zone for another");
    }

    [Fact]
    public async Task An_unknown_stored_zone_reads_as_the_default()
    {
        source.GetAsync("users/1", Arg.Any<CancellationToken>()).Returns(Task.FromResult<SparkRecipientLocalePreference?>(new SparkRecipientLocalePreference("Not/AZone", "en")));
        (await Create(false, source).ForUserAsync("users/1")).Should().Be(new SparkRecipientLocale("UTC", "en"));
    }

    [Fact]
    public void FromCurrentRequest_is_the_viewer_and_outside_a_request_the_defaults()
    {
        Create(true, null).FromCurrentRequest().Should().Be(new SparkRecipientLocale("America/New_York", "fr"));
        Create(false, null).FromCurrentRequest().Should().Be(new SparkRecipientLocale("UTC", "nl"));
    }

    [Fact]
    public async Task Capture_fills_only_the_unset_parts_and_returns_the_effective_locale()
    {
        source.GetAsync("users/1", Arg.Any<CancellationToken>()).Returns(Task.FromResult<SparkRecipientLocalePreference?>(new SparkRecipientLocalePreference(null, "nl-BE")));

        var locale = await Create(true, source).CaptureForUserAsync("users/1");

        locale.Should().Be(new SparkRecipientLocale("America/New_York", "nl-BE"));
        await source.Received(1).SaveIfUnsetAsync("users/1", new SparkRecipientLocale("America/New_York", "fr"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Capture_writes_nothing_when_both_parts_are_chosen()
    {
        source.GetAsync("users/1", Arg.Any<CancellationToken>()).Returns(Task.FromResult<SparkRecipientLocalePreference?>(new SparkRecipientLocalePreference("Europe/Brussels", "nl")));

        (await Create(true, source).CaptureForUserAsync("users/1")).Should().Be(new SparkRecipientLocale("Europe/Brussels", "nl"));
        await source.DidNotReceiveWithAnyArgs().SaveIfUnsetAsync(default!, default!, default);
    }
}
