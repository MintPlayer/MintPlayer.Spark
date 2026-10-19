using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Tests.Hierarchy;

/// <summary>
/// Pins the <c>--spark-verify-model</c> check on <see cref="SparkReferenceTree"/> (WX9). A parent attribute the
/// picker cannot filter on does not fail at run time, it returns every row as every node's children, so the
/// declaration is refused up front.
/// </summary>
public class ReferenceTreeVerificationTests
{
    private static EntityAttributeDefinition Attr(string name, string dataType = "string", string? referenceType = null,
        EShowedOn showedOn = EShowedOn.Query | EShowedOn.PersistentObject, bool? canFilter = null, SparkReferenceTree? tree = null)
        => new() { Id = Guid.NewGuid(), Name = name, DataType = dataType, ReferenceType = referenceType, ShowedOn = showedOn, CanFilter = canFilter, ReferenceTree = tree };

    private static EntityTypeDefinition Model(string name, params EntityAttributeDefinition[] attributes)
        => new() { Id = Guid.NewGuid(), Name = name, ClrType = "Wh." + name, Attributes = attributes };

    private static EntityTypeDefinition Location(EntityAttributeDefinition? parent = null)
        => Model("Location",
            Attr("Code"),
            parent ?? Attr("Parent", "Reference", "Wh.Location"),
            Attr("ChildCount", "number"));

    private static EntityTypeDefinition TaskModel(SparkReferenceTree tree)
        => Model("Task", Attr("Location", "Reference", "Wh.Location", tree: tree));

    [Fact]
    public void A_well_formed_tree_passes()
    {
        var (errors, warnings) = SparkDevelopmentExtensions.DescribeReferenceTreeProblems(
            [Location(), TaskModel(new SparkReferenceTree { ParentAttribute = "Parent", ChildCountAttribute = "ChildCount" })]);

        errors.Should().BeEmpty();
        warnings.Should().BeEmpty();
    }

    [Fact]
    public void An_unknown_parent_attribute_is_an_error()
    {
        var (errors, _) = SparkDevelopmentExtensions.DescribeReferenceTreeProblems(
            [Location(), TaskModel(new SparkReferenceTree { ParentAttribute = "Nope" })]);

        errors.Should().ContainSingle();
    }

    [Fact]
    public void A_parent_hidden_from_queries_is_an_error()
    {
        var (errors, _) = SparkDevelopmentExtensions.DescribeReferenceTreeProblems(
            [Location(Attr("Parent", "Reference", "Wh.Location", showedOn: EShowedOn.PersistentObject)),
             TaskModel(new SparkReferenceTree { ParentAttribute = "Parent" })]);

        errors.Should().ContainSingle();
    }

    [Fact]
    public void A_parent_that_cannot_be_filtered_is_an_error()
    {
        var (errors, _) = SparkDevelopmentExtensions.DescribeReferenceTreeProblems(
            [Location(Attr("Parent", "Reference", "Wh.Location", canFilter: false)),
             TaskModel(new SparkReferenceTree { ParentAttribute = "Parent" })]);

        errors.Should().ContainSingle();
    }

    [Fact]
    public void A_parent_referencing_another_type_is_an_error()
    {
        var (errors, _) = SparkDevelopmentExtensions.DescribeReferenceTreeProblems(
            [Location(Attr("Parent", "Reference", "Wh.Site")),
             TaskModel(new SparkReferenceTree { ParentAttribute = "Parent" })]);

        errors.Should().ContainSingle();
    }

    [Fact]
    public void An_unknown_child_count_attribute_is_an_error()
    {
        var (errors, _) = SparkDevelopmentExtensions.DescribeReferenceTreeProblems(
            [Location(), TaskModel(new SparkReferenceTree { ParentAttribute = "Parent", ChildCountAttribute = "Kids" })]);

        errors.Should().ContainSingle();
    }

    [Fact]
    public void A_tree_on_a_non_reference_attribute_is_only_a_warning()
    {
        var (errors, warnings) = SparkDevelopmentExtensions.DescribeReferenceTreeProblems(
            [Model("Task", Attr("Code", tree: new SparkReferenceTree { ParentAttribute = "Parent" }))]);

        errors.Should().BeEmpty();
        warnings.Should().ContainSingle();
    }
}
