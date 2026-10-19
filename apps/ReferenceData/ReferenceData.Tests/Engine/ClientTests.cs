using System.Text.RegularExpressions;
using MintPlayer.Spark.ReferenceData;
using MintPlayer.Spark.ReferenceData.Client;
using MintPlayer.Spark.ReferenceData.Sources.Belgium;
using MintPlayer.Spark.ReferenceData.Sources.France;
using MintPlayer.Spark.ReferenceData.Sources.Global;

namespace ReferenceData.Tests.Engine;

/// <summary>The consumer side's pure parts (PRD §4.15): address lines from a profile, the profiles' patterns, CLDR names.</summary>
public class ClientTests
{
    [Fact]
    public void Belgian_lines_follow_the_profile_template()
    {
        var address = new Address { StreetName = "Kerkstraat", HouseNumber = "12", Box = "3", PostalCode = "9770", LocalityName = "Kruishoutem" };
        AddressLines.Render(address, BelgiumProfile.Create().AddressLines).Should().Equal("Kerkstraat 12 bus 3", "9770 Kruishoutem");
        address.Box = null;
        AddressLines.Render(address, BelgiumProfile.Create().AddressLines).Should().Equal("Kerkstraat 12", "9770 Kruishoutem");
    }

    [Fact]
    public void French_lines_put_the_number_first()
    {
        var fr = FranceProfile.Create();
        var address = new Address { StreetName = "rue de Rivoli", HouseNumber = "8", Box = "bis", PostalCode = "75004", LocalityName = "Paris" };
        AddressLines.Render(address, fr.AddressLines).Should().Equal("8 bis rue de Rivoli", "75004 Paris");
    }

    [Theory]
    [InlineData("9770", true)]
    [InlineData("0770", false)]
    [InlineData("97700", false)]
    public void The_Belgian_postcode_pattern(string postcode, bool valid)
        => Regex.IsMatch(postcode, BelgiumProfile.Create().PostcodePattern!).Should().Be(valid);

    [Fact]
    public void VAT_territories_match_their_postcodes()
    {
        var fr = FranceProfile.Create().VatTerritories;
        fr.Single(t => t.Code == "FR-GP").PostcodePattern.Should().NotBeNull();
        Regex.IsMatch("97110", fr.Single(t => t.Code == "FR-GP").PostcodePattern!).Should().BeTrue();
        Regex.IsMatch("97150", fr.Single(t => t.Code == "FR-GP").PostcodePattern!).Should().BeFalse();   // Saint-Martin
        Regex.IsMatch("97150", fr.Single(t => t.Code == "FR-MF").PostcodePattern!).Should().BeTrue();
        var de = BaselineProfiles.All().Single(p => p.Code == "de").Profile.VatTerritories;
        de.Select(t => t.Code).Should().BeEquivalentTo(new[] { "DE-HELGOLAND", "DE-BUSINGEN" });
        de.Should().OnlyContain(t => t.Treatment == VatTerritoryTreatment.OutsideVatAndCustoms);
    }

    [Fact]
    public void Server_side_country_names_come_from_the_pinned_CLDR_subset()
    {
        CldrNames.Territory("BE", "nl").Should().Be("België");
        CldrNames.Territory("BE", "fr-BE").Should().Be("Belgique");
        CldrNames.Territory("BE", "xx").Should().Be("Belgium");
        CldrNames.Codes.Should().Contain("LU");
    }
}
