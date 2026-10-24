using System.Net;
using CodeCoverage.GitWriter.Tests._Infrastructure;

namespace CodeCoverage.GitWriter.Tests;

/// <summary>#153 M5: the server host's version tags, over the authenticated HTTP fixture forge.</summary>
public class TagsTests : WriterTestBase
{
    private const string Hash = "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    private static Dictionary<string, object?> TagsRequest(Origin origin, params (string Name, string Commit)[] tags) => new()
    {
        ["provider"] = "github",
        ["repoId"] = "1001",
        ["cloneUrl"] = origin.Url.AbsoluteUri,
        ["credential"] = Credential(Token),
        ["branches"] = new[] { "main" },
        ["tagger"] = new { name = "coverage-bot", email = "bot@example.invalid" },
        ["tags"] = tags.Select(t => new { name = t.Name, commit = t.Commit, apiHash = Hash }).ToArray(),
    };

    [Fact]
    public async Task Pushes_annotated_tags_and_a_rerun_writes_nothing()
    {
        var origin = Seed();
        var main = origin.Ref("refs/heads/main")!;

        var first = await Writer.TagsAsync(TagsRequest(origin, ("Core/v10.0.0", main)));

        first.Outcome.Should().Be("tagged", first.Detail);
        first.Tags.Should().ContainSingle().Which.Outcome.Should().Be("created");
        origin.InBare("rev-parse", "refs/tags/Core/v10.0.0^{commit}").Trim().Should().Be(main);
        origin.InBare("cat-file", "-p", "refs/tags/Core/v10.0.0").Should().Contain("tagger coverage-bot <bot@example.invalid>").And.Contain("api-hash: " + Hash);

        var rerun = await Writer.TagsAsync(TagsRequest(origin, ("Core/v10.0.0", main)));
        rerun.Tags.Should().ContainSingle().Which.Outcome.Should().Be("already-present");
        Directory.GetFileSystemEntries(Writer.Root).Should().BeEmpty("the job directory is deleted when the job ends");
    }

    [Fact]
    public async Task A_commit_off_the_allowed_branches_is_refused()
    {
        var origin = Seed();
        origin.Write("feature.txt", "feature\n");
        var feature = origin.Commit("feature");
        origin.Push("feature");

        var response = await Writer.TagsAsync(TagsRequest(origin, ("Core/v10.0.0", feature)));

        response.Tags.Should().ContainSingle().Which.Outcome.Should().Be("unreachable");
        origin.Ref("refs/tags/Core/v10.0.0").Should().BeNull();
    }

    [Theory]
    [InlineData("name", "Core/10.0.0")]
    [InlineData("name", "Core/v10.0")]
    [InlineData("commit", "HEAD")]
    [InlineData("apiHash", "sha256:short")]
    public async Task A_malformed_tag_is_refused_before_any_git(string field, string value)
    {
        var origin = Seed();
        var tag = new Dictionary<string, object?> { ["name"] = "Core/v10.0.0", ["commit"] = origin.Ref("refs/heads/main"), ["apiHash"] = Hash };
        tag[field] = value;
        var request = TagsRequest(origin);
        request["tags"] = new[] { tag };

        var response = await Writer.PostTagsAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("\"tags\"");
    }
}
