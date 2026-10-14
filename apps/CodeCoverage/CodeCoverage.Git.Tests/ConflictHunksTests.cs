using CodeCoverage.Git.MergeQueue;

namespace CodeCoverage.Git.Tests;

/// <summary>The line-ownership half of the hunk check (<see cref="ConflictHunks"/>): which lines of a JSON side count as
/// an allow-listed key's. The hunks themselves come from real merges in <see cref="MergeQueueResolverTests"/>.</summary>
public class ConflictHunksTests
{
    private static readonly IReadOnlySet<string> RollUps = new HashSet<string> { "modelHash", "contextRoots", "modelFiles" };

    [Fact]
    public void Only_lines_holding_nothing_but_an_allow_listed_top_level_member_are_allowed()
    {
        var text = "{\n  \"version\": 2,\n  \"modelHash\": \"aa\",\n  \"contextRoots\": \"bb\",\n  \"files\": {\n    \"A.json\": \"cc\"\n  }\n}\n";

        var allowed = ConflictHunks.AllowedLineTexts(text, RollUps)!;

        allowed.Should().BeEquivalentTo(["  \"modelHash\": \"aa\",", "  \"contextRoots\": \"bb\","]);
    }

    [Fact]
    public void A_text_that_also_appears_on_a_forbidden_line_is_not_allowed()
    {
        // The same text once as the roll-up and once inside `files` (valid JSON, odd indentation): a hunk holding that
        // text could be either line, so it is not trusted.
        var text = "{\n  \"modelHash\": \"aa\",\n  \"files\": {\n  \"modelHash\": \"aa\",\n    \"A.json\": \"cc\"\n  }\n}\n";

        ConflictHunks.AllowedLineTexts(text, RollUps)!.Should().BeEmpty();
    }

    [Fact]
    public void A_line_shared_with_another_member_or_a_brace_is_not_allowed()
    {
        ConflictHunks.AllowedLineTexts("{\"modelHash\": \"aa\", \"files\": {}}\n", RollUps)!.Should().BeEmpty();
        ConflictHunks.AllowedLineTexts("{\"modelHash\": \"aa\",\n  \"version\": 2}\n", RollUps)!.Should().BeEmpty();
    }

    [Fact]
    public void A_multi_line_allow_listed_value_is_allowed_on_every_line()
    {
        var text = "{\n  \"modelFiles\": [\n    \"x\"\n  ],\n  \"version\": 2\n}\n";

        ConflictHunks.AllowedLineTexts(text, RollUps)!.Should().BeEquivalentTo(["  \"modelFiles\": [", "    \"x\"", "  ],"]);
    }

    [Theory]
    [InlineData("[1, 2]\n")]
    [InlineData("{\n  \"modelHash\": \"aa\",\n}\n")]
    [InlineData("{\n  // comment\n  \"modelHash\": \"aa\"\n}\n")]
    [InlineData("{\n  \"modelHash\": \"aa\"\n}\n{}\n")]
    [InlineData("")]
    public void Anything_but_one_strict_JSON_object_is_refused(string text)
        => ConflictHunks.AllowedLineTexts(text, RollUps).Should().BeNull();

    [Fact]
    public void Diff3_regions_parse_and_malformed_ones_are_refused()
    {
        var merged = "{\n<<<<<<< a\n  \"modelHash\": \"a1\",\n||||||| base\n  \"modelHash\": \"o\",\n=======\n  \"modelHash\": \"b1\",\n>>>>>>> b\n}\n";

        var hunk = ConflictHunks.Parse(merged)!.Should().ContainSingle().Which;
        hunk.Ours.Should().Equal(["  \"modelHash\": \"a1\","]);
        hunk.Base.Should().Equal(["  \"modelHash\": \"o\","]);
        hunk.Theirs.Should().Equal(["  \"modelHash\": \"b1\","]);

        ConflictHunks.Parse("<<<<<<< a\nx\n=======\ny\n>>>>>>> b\n").Should().BeNull("a merge-style region without the base section is not what the queue asked for");
        ConflictHunks.Parse("<<<<<<< a\nx\n||||||| o\n=======\n").Should().BeNull("an unterminated region");
    }
}
