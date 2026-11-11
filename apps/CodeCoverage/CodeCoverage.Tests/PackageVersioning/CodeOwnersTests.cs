using CodeCoverage.PackageVersioning;
using Xunit;

namespace CodeCoverage.Tests.PackageVersioning;

/// <summary>Release lines recovery plan, segment F: who owns a held backport's files, by GitHub's CODEOWNERS rules.</summary>
public class CodeOwnersTests
{
    private const string File = """
        # The default owners.
        *                 @acme/core
        *.md              @docs-writer
        /apps/            @apps-lead   # anchored at the root
        docs              @docs-lead
        src/Guard.cs      @guard @acme/security
        /generated/
        !negated          @nobody
        [Bb]uild          @nobody
        """;

    private static readonly CodeOwners Owners = CodeOwners.Parse(File);

    [Theory]
    [InlineData("src/Program.cs", "@acme/core")]
    [InlineData("README.md", "@docs-writer")]
    [InlineData("src/deep/notes.md", "@docs-writer")]
    [InlineData("apps/Fleet/Program.cs", "@apps-lead")]
    [InlineData("libs/apps/Program.cs", "@acme/core")]
    [InlineData("docs/guide.txt", "@docs-lead")]
    [InlineData("libs/docs/guide.txt", "@docs-lead")]
    [InlineData("src/Guard.cs", "@guard,@acme/security")]
    [InlineData("lib/src/Guard.cs", "@acme/core")]
    public void The_last_matching_line_owns_a_path(string path, string expected)
        => Owners.OwnersOf(path).Should().Equal(expected.Split(','));

    [Fact]
    public void A_line_without_owners_unowns_its_paths()
        => Owners.OwnersOf("generated/Model.cs").Should().BeEmpty();

    [Theory]
    [InlineData("negated")]
    [InlineData("build")]
    public void A_line_that_is_not_codeowners_syntax_is_skipped(string path)
        => Owners.OwnersOf(path).Should().Equal(["@acme/core"]);

    [Fact]
    public void The_owners_of_several_paths_are_listed_once_in_first_seen_order()
        => Owners.OwnersOf(["src/Guard.cs", "README.md", "src/Other.cs", "src/Guard.cs"])
            .Should().Equal(["@guard", "@acme/security", "@docs-writer", "@acme/core"]);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("# only a comment\n\n/\n")]
    public void An_empty_file_owns_nothing(string? text)
        => CodeOwners.Parse(text).OwnersOf("src/Program.cs").Should().BeEmpty();

    [Fact]
    public void GitHub_looks_in_the_github_folder_first()
        => CodeOwners.Locations.Should().Equal([".github/CODEOWNERS", "CODEOWNERS", "docs/CODEOWNERS"]);
}
