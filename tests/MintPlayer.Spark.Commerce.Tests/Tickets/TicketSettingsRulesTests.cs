using MintPlayer.Spark.Commerce.Ticketing.Tickets;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Models;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Services;

namespace MintPlayer.Spark.Commerce.Tests.Tickets;

/// <summary>The bounds of an event's ticket settings (D11, D30, GC7), of check-in lists (GB3) and of the package options (S8). Pure.</summary>
public class TicketSettingsRulesTests
{
    private static readonly SparkTicketingTicketsOptions Defaults = new();

    [Fact]
    public void Settings_within_the_bounds_pass()
        => TicketEventSettingsInterceptor.Problem(new TicketEventSettings
        {
            EventId = "TicketEvents/1", DeliveryLeadTimeHours = 72, TransferFeeBasisPoints = 500, FreezeBeforeDoorsMinutes = 120,
            TemplateAccentColor = "#0a7cff", TemplateAccentTextColor = "#FFFFFF",
        }, Defaults).HasValue.Should().BeFalse();

    [Theory]
    [InlineData(-1, null, null, null, nameof(TicketEventSettings.DeliveryLeadTimeHours))]
    [InlineData(24 * 61, null, null, null, nameof(TicketEventSettings.DeliveryLeadTimeHours))]
    [InlineData(null, 1_001, null, null, nameof(TicketEventSettings.TransferFeeBasisPoints))]
    [InlineData(null, -5, null, null, nameof(TicketEventSettings.TransferFeeBasisPoints))]
    [InlineData(null, null, -1, null, nameof(TicketEventSettings.FreezeBeforeDoorsMinutes))]
    [InlineData(null, null, null, "red", nameof(TicketEventSettings.TemplateAccentColor))]
    [InlineData(null, null, null, "#12345", nameof(TicketEventSettings.TemplateAccentColor))]
    public void Settings_outside_the_bounds_are_refused(int? leadHours, int? feeBasisPoints, int? freezeMinutes, string? accent, string attribute)
    {
        var problem = TicketEventSettingsInterceptor.Problem(new TicketEventSettings
        {
            EventId = "TicketEvents/1", DeliveryLeadTimeHours = leadHours, TransferFeeBasisPoints = feeBasisPoints,
            FreezeBeforeDoorsMinutes = freezeMinutes, TemplateAccentColor = accent,
        }, Defaults);
        problem.HasValue.Should().BeTrue();
        problem!.Value.Attribute.Should().Be(attribute);
    }

    [Fact]
    public void A_list_names_only_its_events_dates_and_types_and_a_zone_names_its_types()
    {
        var dates = new HashSet<string> { "TicketEventDates/1" };
        var types = new HashSet<string> { "TicketTypes/vip" };
        TicketCheckInListInterceptor.Problem(new TicketCheckInList { Name = "Gate", DateIds = ["TicketEventDates/1"] }, dates, types).HasValue.Should().BeFalse();
        TicketCheckInListInterceptor.Problem(new TicketCheckInList { Name = "" }, dates, types)!.Value.Attribute.Should().Be(nameof(TicketCheckInList.Name));
        TicketCheckInListInterceptor.Problem(new TicketCheckInList { Name = "Gate", DateIds = ["TicketEventDates/9"] }, dates, types)!.Value.Attribute.Should().Be(nameof(TicketCheckInList.DateIds));
        TicketCheckInListInterceptor.Problem(new TicketCheckInList { Name = "Gate", TicketTypeIds = ["TicketTypes/other"] }, dates, types)!.Value.Attribute.Should().Be(nameof(TicketCheckInList.TicketTypeIds));
        TicketCheckInListInterceptor.Problem(new TicketCheckInList { Name = "VIP", Kind = ETicketCheckInListKind.Zone }, dates, types)!.Value.Attribute.Should().Be(nameof(TicketCheckInList.TicketTypeIds));
        TicketCheckInListInterceptor.Problem(new TicketCheckInList { Name = "VIP", Kind = ETicketCheckInListKind.Zone, TicketTypeIds = ["TicketTypes/vip"] }, dates, types).HasValue.Should().BeFalse();
    }

    [Fact]
    public void The_default_options_start_and_S8s_cost_is_the_default()
    {
        SparkTicketingTicketsExtensions.Problems(Defaults).Should().BeEmpty();
        Defaults.Secrets.Iterations.Should().Be(50_000);
    }

    [Fact]
    public void Options_that_make_no_sense_are_refused_at_startup()
    {
        var options = new SparkTicketingTicketsOptions();
        options.Secrets.Iterations = 1_000;
        options.Delivery.TicketPagePath = "/tickets";
        options.Scanner.ListPageSize = 50;
        options.Exchange.MaxTransferFeeBasisPoints = 20_000;
        var problems = SparkTicketingTicketsExtensions.Problems(options);
        problems.Should().HaveCount(4);
        problems.Should().Contain(p => p.Contains("Iterations"));
        problems.Should().Contain(p => p.Contains("TicketPagePath"));
        problems.Should().Contain(p => p.Contains("ListPageSize"));
        problems.Should().Contain(p => p.Contains("MaxTransferFeeBasisPoints"));
    }
}
