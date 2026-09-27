using MintPlayer.Spark.Endpoints.PersistentObject;

namespace MintPlayer.Spark.Tests.Endpoints.PersistentObject;

/// <summary>
/// The <c>triggeredBy</c> grammar of a refresh: <c>Attr[i].Column</c> for an AsDetail row,
/// <c>Attr.Column</c> for a single embedded object, and anything else falls through to the root
/// hook. The grammar is deliberately permissive; the model decides what resolves.
/// </summary>
public class NestedTriggerParseTests
{
    [Theory]
    [InlineData("Lines[2].Quantity", "Lines", 2, "Quantity")]
    [InlineData("Lines[0].X", "Lines", 0, "X")]
    [InlineData("Gate.ProjectMode", "Gate", null, "ProjectMode")]
    [InlineData("Gate.Inner.X", "Gate", null, "Inner.X")]
    public void A_nested_trigger_is_split_into_attribute_row_and_column(
        string triggeredBy, string attribute, int? rowIndex, string column)
    {
        var parsed = NestedTrigger.TryParse(triggeredBy);

        parsed.HasValue.Should().BeTrue();
        parsed!.Value.Attribute.Should().Be(attribute);
        (parsed.Value.RowIndex ?? -1).Should().Be(rowIndex ?? -1);
        parsed.Value.Column.Should().Be(column);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Name")]
    [InlineData("[0].Name")]
    [InlineData("Lines[x].Quantity")]
    [InlineData("Lines[1]")]
    [InlineData("Lines[1].")]
    [InlineData("Lines[1]Quantity")]
    [InlineData("Lines[1")]
    [InlineData(".Column")]
    [InlineData("Gate.")]
    public void Anything_else_falls_through_to_the_root_hook(string? triggeredBy)
    {
        NestedTrigger.TryParse(triggeredBy).HasValue.Should().BeFalse();
    }
}
