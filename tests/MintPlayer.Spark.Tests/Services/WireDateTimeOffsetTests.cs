using MintPlayer.Spark.Services;
using MintPlayer.Spark.Tests._Infrastructure;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// The one reading of a wire date that saves and filters share (PRD D2): an offset that is present is
/// kept, an absent one means UTC — on a server that is not in UTC, which is the only place the
/// difference shows (D11).
/// </summary>
[Collection(LocalZoneCollection.Name)]
public sealed class WireDateTimeOffsetTests : IDisposable
{
    private readonly LocalZone _zone = new();

    public void Dispose() => _zone.Dispose();

    [Theory]
    [InlineData("2027-03-01T09:00:00", "2027-03-01T09:00:00.0000000+00:00")]
    [InlineData("2027-03-01", "2027-03-01T00:00:00.0000000+00:00")]
    [InlineData("2027-07-01T09:00:00", "2027-07-01T09:00:00.0000000+00:00")]
    public void A_value_without_an_offset_is_utc_not_server_local(string wire, string expected)
    {
        LocalZone.RequireNonUtc(DateTime.Parse(expected, null, System.Globalization.DateTimeStyles.AdjustToUniversal));

        var parsed = WireDateTimeOffset.Parse(wire);

        parsed.EqualsExact(DateTimeOffset.Parse(expected)).Should().BeTrue($"expected {expected}, got {parsed:O}");
    }

    [Theory]
    [InlineData("2027-03-01T09:00:00-05:00")]
    [InlineData("2027-03-01T09:00:00.5+05:45")]
    [InlineData("2027-03-02T01:00:00.0000000+12:00")]
    [InlineData("2027-03-01T09:00:00+00:00")]
    public void An_offset_that_is_present_is_kept(string wire)
    {
        var parsed = WireDateTimeOffset.Parse(wire);

        parsed.EqualsExact(DateTimeOffset.Parse(wire, System.Globalization.CultureInfo.InvariantCulture)).Should().BeTrue();
    }

    [Fact]
    public void Z_is_utc()
        => WireDateTimeOffset.Parse("2027-03-01T09:00:00Z").EqualsExact(new DateTimeOffset(2027, 3, 1, 9, 0, 0, TimeSpan.Zero)).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a date")]
    public void Garbage_does_not_parse(string? wire)
        => WireDateTimeOffset.TryParse(wire, out _).Should().BeFalse();
}
