using System.Drawing;
using CodeCoverage.Entities;
using CodeCoverage.PrSize;
using Xunit;

namespace CodeCoverage.Tests.PrSize;

/// <summary>A scale change reconciles only when what the forge should carry differs (docs/coverage_pr_size_labels_PRD.md D4.2).</summary>
public class PrSizeLabelSwitchTests
{
    [Fact]
    public void An_unchanged_scale_is_not_a_change()
        => PrSizeLabelSwitch.ScaleChanged(PrSizeDefaults.Scale(), PrSizeDefaults.Scale()).Should().BeFalse();

    [Fact]
    public void A_renamed_row_is_a_change()
    {
        var after = PrSizeDefaults.Scale();
        after.Single(r => r.Id == "m").Name = "size/medium";
        PrSizeLabelSwitch.ScaleChanged(PrSizeDefaults.Scale(), after).Should().BeTrue();
    }

    [Fact]
    public void A_recoloured_row_is_a_change()
    {
        var after = PrSizeDefaults.Scale();
        after.Single(r => r.Id == "m").Color = ColorTranslator.FromHtml("#123456");
        PrSizeLabelSwitch.ScaleChanged(PrSizeDefaults.Scale(), after).Should().BeTrue();
    }

    [Fact]
    public void A_moved_threshold_is_a_change_because_it_is_in_the_description()
    {
        var after = PrSizeDefaults.Scale();
        after.Single(r => r.Id == "m").MinLines = 40;
        PrSizeLabelSwitch.ScaleChanged(PrSizeDefaults.Scale(), after).Should().BeTrue();
    }
}
