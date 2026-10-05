using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Authorization.Identity;
using Raven.Client.Documents.Conventions;

namespace MintPlayer.Spark.Tests.Queries;

/// <summary>
/// The gate every collection name and field path passes before it is spliced into RQL or a patch
/// script (#264). Values never reach it — they are parameters — so what it must do is accept every
/// identifier actually in use and refuse anything that could end the identifier and start new syntax.
/// </summary>
public class RqlIdentifierTests
{
    /// <summary>Every collection spliced by the framework, its authorization package and the apps.</summary>
    [Theory]
    [InlineData("Repositories")]
    [InlineData("Accounts")]
    [InlineData("PullRequestFeedbacks")]
    [InlineData("Commits")]
    [InlineData("Builds")]
    [InlineData("BuildTreeSummaries")]
    [InlineData("CommitAssemblies")]
    [InlineData("FileCoverages")]
    [InlineData("GitHubProjects")]
    [InlineData("People")]
    [InlineData("OidcApplications")]
    [InlineData("Snake_case2")]
    public void Collection_accepts_the_names_in_use(string name)
    {
        RqlIdentifier.Collection(name).Should().Be(name);
    }

    /// <summary>The convention, not a literal, names these two — so ask the convention.</summary>
    [Fact]
    public void Collection_accepts_the_conventional_user_and_role_collections()
    {
        var conventions = new DocumentConventions();

        foreach (var type in new[] { typeof(SparkUser), typeof(SparkRole) })
        {
            var name = conventions.FindCollectionName(type);
            RqlIdentifier.Collection(name).Should().Be(name);
        }
    }

    [Theory]
    [InlineData("Account")]
    [InlineData("LatestBuildId")]
    [InlineData("Origin.FromBuildId")]
    [InlineData("Jobs")]
    [InlineData("Jobs[].Id")]
    [InlineData("Lines[].Children[].Key")]
    [InlineData("Sessions[]")]
    public void FieldPath_accepts_the_paths_in_use(string path)
    {
        RqlIdentifier.FieldPath(path).Should().Be(path);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("x' or true or '")]
    [InlineData("Users' update { this.IsAdmin = true } //")]
    [InlineData("\") update { this.X = 1 } //")]
    [InlineData("Name; delete")]
    [InlineData("Users as d")]
    [InlineData("Users//")]
    [InlineData("Users/*x*/")]
    [InlineData("Users\nupdate")]
    [InlineData("Users\r")]
    [InlineData("Users{")]
    [InlineData("Users}")]
    [InlineData("Users\\")]
    [InlineData("Users\"")]
    [InlineData("Users.Name")]
    [InlineData("Users[]")]
    [InlineData("1Users")]
    [InlineData("_Users")]
    [InlineData("Us-ers")]
    [InlineData("@all_docs")]
    [InlineData("Usérs")]
    public void Collection_refuses_anything_else(string? name)
    {
        var act = () => RqlIdentifier.Collection(name!);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData(".")]
    [InlineData("Origin.")]
    [InlineData(".Origin")]
    [InlineData("Origin..Id")]
    [InlineData("x' or true or '")]
    [InlineData("Name; delete")]
    [InlineData("Name != null or true")]
    [InlineData("Name) update { this.X = 1 } //")]
    [InlineData("Name // comment")]
    [InlineData("Name/*c*/")]
    [InlineData("Name\nor true")]
    [InlineData("Name{")]
    [InlineData("Name}")]
    [InlineData("Jobs[0].Id")]
    [InlineData("Jobs[]]")]
    [InlineData("Jobs[][]")]
    [InlineData("[].Id")]
    [InlineData("Jobs[ ].Id")]
    [InlineData("1Name")]
    [InlineData("Na me")]
    public void FieldPath_refuses_anything_else(string? path)
    {
        var act = () => RqlIdentifier.FieldPath(path!);

        act.Should().Throw<ArgumentException>();
    }
}
