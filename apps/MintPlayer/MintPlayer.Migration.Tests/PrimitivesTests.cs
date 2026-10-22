using System.Security.Cryptography;
using System.Text;
using MintPlayer.Migration.Target;
using MintPlayer.Migration.Transform;
using Newtonsoft.Json.Linq;

namespace MintPlayer.Migration.Tests;

public class PrimitivesTests
{
    private static readonly TimeZoneInfo Amsterdam = LegacyTime.Resolve("Europe/Amsterdam");
    private static readonly Guid User = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void Ids_match_seed_catalog_and_spark_conventions()
    {
        (Ids.Subject("artist", 12)).Should().Be("Artists/12");
        (Ids.Subject("person", 3)).Should().Be("People/3");
        (Ids.Subject("song", 40)).Should().Be("Songs/40");
        (Ids.User(User)).Should().Be("SparkUsers/11111111-1111-1111-1111-111111111111");
        (Ids.UserLikes(Ids.User(User))).Should().Be("UserLikes/SparkUsers/11111111-1111-1111-1111-111111111111");
        Action act = () => Ids.Subject("album", 1);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Email_reservation_key_is_lowercased_normalized_email()
        => (Ids.EmailReservation("SOMEONE@EXAMPLE.COM")).Should().Be("emails/someone@example.com");

    [Theory]
    [InlineData(null, false)]                                       // Subjects: NULL = live
    [InlineData("00000000-0000-0000-0000-000000000000", false)]    // Tags etc.: zero GUID = live
    [InlineData("22222222-2222-2222-2222-222222222222", false)]    // dangling user id = live (navigation is null)
    [InlineData("11111111-1111-1111-1111-111111111111", true)]     // joins a user = deleted
    public void Soft_delete_is_user_delete_id_joining_an_existing_user(string? userDeleteId, bool deleted)
        => (SoftDelete.IsDeleted(userDeleteId is null ? null : Guid.Parse(userDeleteId), new HashSet<Guid> { User })).Should().Be(deleted);

    [Fact]
    public void Local_times_convert_to_utc_with_dst()
    {
        (LegacyTime.ToUtc(new DateTime(2021, 1, 15, 10, 0, 0), Amsterdam)).Should().Be(new DateTimeOffset(2021, 1, 15, 9, 0, 0, TimeSpan.Zero));
        (LegacyTime.ToUtc(new DateTime(2021, 7, 15, 10, 0, 0), Amsterdam)).Should().Be(new DateTimeOffset(2021, 7, 15, 8, 0, 0, TimeSpan.Zero));
        // spring-forward gap 02:30 does not exist → treated as 03:30 CEST
        (LegacyTime.ToUtc(new DateTime(2021, 3, 28, 2, 30, 0), Amsterdam)).Should().Be(new DateTimeOffset(2021, 3, 28, 1, 30, 0, TimeSpan.Zero));
        // fall-back 02:30 is ambiguous → first (CEST) occurrence
        (LegacyTime.ToUtc(new DateTime(2021, 10, 31, 2, 30, 0), Amsterdam)).Should().Be(new DateTimeOffset(2021, 10, 31, 0, 30, 0, TimeSpan.Zero));
        (LegacyTime.ToUtcOrNull(DateTime.MinValue, Amsterdam)).Should().NotHaveValue();
    }

    [Fact]
    public void Dates_drop_min_value_and_fix_utc_shifted_midnights()
    {
        (LegacyTime.ToDate(DateTime.MinValue, Amsterdam, out _)).Should().NotHaveValue();
        (LegacyTime.ToDate(null, Amsterdam, out _)).Should().NotHaveValue();
        (LegacyTime.ToDate(new DateTime(1980, 5, 3), Amsterdam, out var s1)).Should().Be(new DateOnly(1980, 5, 3));
        s1.Should().BeFalse();
        // 22:00 UTC in summer = local midnight of the next day
        (LegacyTime.ToDate(new DateTime(1980, 5, 3, 22, 0, 0), Amsterdam, out var s2)).Should().Be(new DateOnly(1980, 5, 4));
        s2.Should().BeTrue();
    }

    [Fact]
    public void Colors_drop_alpha_and_report_it()
    {
        var c = Colors.FromLegacy(unchecked((int)0x80FF8800), out var alpha);
        alpha.Should().BeTrue();
        ((c.A, c.R, c.G, c.B)).Should().Be((255, 255, 136, 0));
        Colors.FromLegacy(unchecked((int)0xFF00B900), out var opaque);
        opaque.Should().BeFalse();
    }

    [Fact]
    public void Spark_color_converter_writes_rrggbb()
    {
        var json = JObject.FromObject(new MintPlayer.Entities.TagCategory { Color = System.Drawing.Color.FromArgb(255, 0, 185, 0) },
            SparkStore.CreateComparisonSerializer());
        ((string?)json["Color"]).Should().Be("#00b900");
    }

    [Fact]
    public void Recovery_code_hash_matches_spark_master_user_store()
    {
        // UserStore.HashRecoveryCode: Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code)))
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("ABCDE-12345"))).ToLowerInvariant();
        (RecoveryCodes.Hash("ABCDE-12345")).Should().Be(expected);
        expected.Length.Should().Be(64);
        (RecoveryCodes.Split("a;b;;c")).Should().Equal(["a", "b", "c"]);
        (RecoveryCodes.Split(null)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abc123", true)]
    [InlineData("https://youtu.be/abc123", true)]
    [InlineData("https://vimeo.com/123456", true)]
    [InlineData("https://www.dailymotion.com/video/x7abc", true)]
    [InlineData("https://soundcloud.com/artist/track", true)]
    [InlineData("https://open.spotify.com/track/4uLU6hMCjMI75M1A2tKUQC", true)]
    [InlineData("https://example.com/file.mp3", true)]
    [InlineData("https://en.wikipedia.org/wiki/Queen", false)]
    [InlineData("https://genius.com/Some-lyrics", false)]
    [InlineData("https://vimeo.com/channels/staff", false)]
    [InlineData("", false)]
    public void Playability_mirrors_the_video_player_plugins(string url, bool playable)
        => (Playability.IsPlayable(url)).Should().Be(playable);

    [Fact]
    public void First_playable_follows_media_order()
        => (Playability.FirstPlayable(["https://en.wikipedia.org/wiki/X", "https://youtu.be/x", "https://vimeo.com/1"])).Should().Be("https://youtu.be/x");
}
