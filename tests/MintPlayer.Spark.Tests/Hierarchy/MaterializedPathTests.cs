using MintPlayer.Spark.Abstractions.Hierarchy;

namespace MintPlayer.Spark.Tests.Hierarchy;

/// <summary>
/// Pins <see cref="MaterializedPath"/> (warehouse PRD WX9): the path convention every hierarchical
/// reference (site → zone → aisle → rack → level → bin) builds on.
/// </summary>
public class MaterializedPathTests
{
    [Fact]
    public void Child_of_null_is_a_root()
        => MaterializedPath.Child(null, "site").Should().Be("site");

    [Fact]
    public void Child_appends_a_segment()
        => MaterializedPath.Child("site/zone", "aisle").Should().Be("site/zone/aisle");

    [Fact]
    public void Combine_joins_segments_root_first()
        => MaterializedPath.Combine(["BRU", "A", "01", "03"]).Should().Be("BRU/A/01/03");

    [Theory]
    [InlineData("")]
    [InlineData(" A")]
    [InlineData("A ")]
    [InlineData("A/B")]
    public void A_malformed_segment_is_refused(string segment)
    {
        var act = () => MaterializedPath.Child("site", segment);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("/A")]
    [InlineData("A/")]
    [InlineData("A//B")]
    [InlineData("A/ B")]
    public void A_malformed_path_is_refused(string path)
    {
        MaterializedPath.IsValid(path).Should().BeFalse();
        var act = () => MaterializedPath.Depth(path);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("site", 1)]
    [InlineData("site/zone", 2)]
    [InlineData("site/zone/aisle/rack/level/bin", 6)]
    public void Depth_counts_segments(string path, int depth)
        => MaterializedPath.Depth(path).Should().Be(depth);

    [Fact]
    public void Parent_of_a_root_is_null()
        => MaterializedPath.Parent("site").Should().BeNull();

    [Fact]
    public void Parent_drops_the_last_segment()
        => MaterializedPath.Parent("site/zone/aisle").Should().Be("site/zone");

    [Fact]
    public void Leaf_is_the_last_segment()
        => MaterializedPath.Leaf("site/zone/aisle").Should().Be("aisle");

    [Fact]
    public void Ancestors_are_listed_root_first_without_the_node_itself()
        => MaterializedPath.Ancestors("a/b/c").Should().Equal("a", "a/b");

    [Fact]
    public void A_root_has_no_ancestors()
        => MaterializedPath.Ancestors("a").Should().BeEmpty();

    [Fact]
    public void Segments_split_the_path()
        => MaterializedPath.Segments("a/b/c").Should().Equal("a", "b", "c");

    /// <summary>
    /// The reason <see cref="MaterializedPath.DescendantPrefix"/> exists: a bare-path prefix makes
    /// <c>A/10</c> a "descendant" of <c>A/1</c>.
    /// </summary>
    [Fact]
    public void A_sibling_sharing_a_prefix_is_not_a_descendant()
    {
        MaterializedPath.IsDescendantOf("A/10", "A/1").Should().BeFalse();
        MaterializedPath.IsDescendantOf("A/1/x", "A/1").Should().BeTrue();
    }

    [Fact]
    public void A_node_is_not_its_own_descendant_but_is_self_or_descendant()
    {
        MaterializedPath.IsDescendantOf("A/1", "A/1").Should().BeFalse();
        MaterializedPath.IsSelfOrDescendantOf("A/1", "A/1").Should().BeTrue();
    }

    [Fact]
    public void DescendantPrefix_ends_with_the_separator()
        => MaterializedPath.DescendantPrefix("A/1").Should().Be("A/1/");

    [Theory]
    [InlineData("site/A", "site/A", "site/B", "site/B/A")]
    [InlineData("site/A/01/03", "site/A", "site/B", "site/B/A/01/03")]
    [InlineData("site/A/01", "site/A", null, "A/01")]
    [InlineData("site/A/01", "site/A/01", "other", "other/01")]
    public void Reparent_keeps_the_moved_root_segment_and_relative_paths(string path, string movedRoot, string? newParent, string expected)
        => MaterializedPath.Reparent(path, movedRoot, newParent).Should().Be(expected);

    [Fact]
    public void Reparent_refuses_a_path_outside_the_moved_subtree()
    {
        var act = () => MaterializedPath.Reparent("site/A10", "site/A", "site/B");
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("site/A")]
    [InlineData("site/A/01")]
    public void A_node_cannot_move_into_its_own_subtree(string newParent)
    {
        var act = () => MaterializedPath.ReparentRoot("site/A", newParent);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Moving_next_to_a_prefix_sharing_sibling_is_allowed()
        => MaterializedPath.ReparentRoot("site/A", "site/A10").Should().Be("site/A10/A");
}
