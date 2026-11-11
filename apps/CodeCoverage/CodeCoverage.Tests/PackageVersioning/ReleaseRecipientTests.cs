using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.PackageVersioning;
using CodeCoverage.Versioning.Releases;
using Xunit;

namespace CodeCoverage.Tests.PackageVersioning;

/// <summary>Segment P: which lines and which commit a <c>/release</c> takes (P2, P4), and the comment it posts.</summary>
public class ReleaseRecipientTests
{
    private static readonly ReleaseLinePattern Pattern = ReleaseLinePattern.Default;

    private static readonly IReadOnlyList<ReleaseLine> Current
        = Pattern.CurrentLines(["release/dotnet-10.x", "release/dotnet-11.x", "release/angular-22.x"]);

    private static ReleaseCommentRequest Request(bool all = false, string? sha = null, params string[] lines) => new(lines, all, sha, []);

    private static ForgeWorkflowRun Run(long id, string sha, string? conclusion, string status = "completed")
        => new(id, status, conclusion, sha, "master", $"https://example.invalid/runs/{id}");

    [Fact]
    public void Without_names_every_current_line_is_selected()
    {
        ReleaseRecipient.Select(Request(), Pattern, Current)!.Select(l => l.ToString()).Should().Equal(["angular-22.x", "dotnet-11.x"]);
        ReleaseRecipient.Select(Request(all: true), Pattern, Current)!.Should().HaveCount(2);
    }

    [Fact]
    public void A_named_line_is_taken_by_its_name_or_its_branch_once()
    {
        ReleaseRecipient.Select(Request(false, null, "dotnet-11.x", "release/dotnet-11.x"), Pattern, Current)!
            .Select(l => l.ToString()).Should().Equal(["dotnet-11.x"]);
    }

    [Theory]
    [InlineData("dotnet-10.x")]
    [InlineData("php-1.x")]
    [InlineData("nonsense")]
    public void A_past_or_unknown_line_refuses_the_request(string name)
        => ReleaseRecipient.Select(Request(false, null, name), Pattern, Current).Should().BeNull();

    [Fact]
    public void The_newest_successful_run_is_green_or_the_one_whose_sha_the_comment_names()
    {
        IReadOnlyList<ForgeWorkflowRun> runs =
        [
            Run(5, "eeee000000000000000000000000000000000000", null, "in_progress"),
            Run(4, "dddd000000000000000000000000000000000000", "failure"),
            Run(3, "cccc000000000000000000000000000000000000", "success"),
            Run(2, "bbbb000000000000000000000000000000000000", "success"),
        ];

        ReleaseRecipient.Green(runs, null)!.Id.Should().Be(3);
        ReleaseRecipient.Green(runs, "BBBB000")!.Id.Should().Be(2);
        ReleaseRecipient.Green(runs, "dddd000").Should().BeNull("its run failed");
    }

    [Fact]
    public void The_report_lists_each_line_the_line_only_paths_and_the_newer_commits_left_out()
    {
        IReadOnlyList<ForgeWorkflowRun> runs =
        [
            Run(4, "dddd000000000000000000000000000000000000", "failure"),
            Run(3, "cccc000000000000000000000000000000000000", "success"),
        ];
        var result = new GitWriterPromoteResult(GitWriterPromoteResult.Promoted, null,
        [
            new("release/dotnet-11.x", GitWriterPromoteLineResult.Promoted, "1111000000000000000000000000000000000000", [], []),
            new("release/angular-22.x", GitWriterPromoteLineResult.LineOnly, "2222000000000000000000000000000000000000", ["README.md"], ["3333 line: readme"]),
        ], null, 10);

        var text = ReleaseRecipient.Report(new Repository { FullName = "acme/widgets" }, "master", "release.yml", runs[1], runs, result);

        text.Should().Contain("[cccc00000000](https://github.com/acme/widgets/commit/cccc000000000000000000000000000000000000)")
            .And.Contain("- `release/dotnet-11.x`: promoted (111100000000)")
            .And.Contain("- `release/angular-22.x`: **not promoted**")
            .And.Contain("- `README.md`").And.Contain("- 3333 line: readme")
            .And.Contain("1 newer commit on master is left out");
    }

    [Fact]
    public void The_report_names_placed_tags_refused_versions_and_versions_no_line_takes()
    {
        IReadOnlyList<ForgeWorkflowRun> runs = [Run(3, "cccc000000000000000000000000000000000000", "success")];
        var result = new GitWriterPromoteResult(GitWriterPromoteResult.Promoted, null,
        [
            new("release/dotnet-11.x", GitWriterPromoteLineResult.Promoted, "1111000000000000000000000000000000000000", [], [], Tags: ["Acme.Core/v11.4.0"]),
            new("release/angular-22.x", GitWriterPromoteLineResult.VersionRefused, "2222000000000000000000000000000000000000", [], [],
                "Not released: @acme/ng/v21.0.0 does not have release/angular-22.x's major 22."),
        ], null, 10, [new("Nope", "1.0.0", "Nope is no package at cccc00000000.")]);

        var text = ReleaseRecipient.Report(new Repository { FullName = "acme/widgets" }, "master", "release.yml", runs[0], runs, result);

        text.Should().Contain("promoted (111100000000) with `Acme.Core/v11.4.0`")
            .And.Contain("- `release/angular-22.x`: **not promoted**. Not released: @acme/ng/v21.0.0 does not have")
            .And.Contain("`Nope@1.0.0` was not placed: Nope is no package");
    }

    [Fact]
    public void A_record_id_is_per_repository_and_keeps_the_scope_out()
    {
        var id = ReleaseVersion.DocumentId("Repositories/github/42", "@acme/ng/v22.1.0");
        id.Should().StartWith(ReleaseVersion.Prefix("Repositories/github/42")).And.NotContain("@");
        id.Should().NotBe(ReleaseVersion.DocumentId("Repositories/github/43", "@acme/ng/v22.1.0"));
    }

    [Fact]
    public void The_hand_tag_comment_names_the_pusher_and_what_happens_next()
    {
        CodeCoverage.Versioning.Versions.VersionTag.TryParse("Acme.Core/v11.4.0", out var tag).Should().BeTrue();
        HandTagRecipient.Comment("octo", tag, "release/dotnet-11.x", null).Should().Contain("@octo").And.Contain("next promotion of `release/dotnet-11.x`");
        HandTagRecipient.Comment("octo", tag, null, "it is published already").Should().Contain("was removed: it is published already");
    }

    private sealed class Registry(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(answer(request));
        }
    }

    private static HttpResponseMessage Json(string body) => new(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) };

    [Fact]
    public async Task A_nuget_id_is_asked_on_nuget_and_found_in_its_version_list()
    {
        var registry = new Registry(r => r.RequestUri!.Host == "api.nuget.org" ? Json("""{ "versions": ["11.3.0", "11.4.0"] }""") : new(System.Net.HttpStatusCode.NotFound));

        (await PublishedVersions.ExistsAsync(new HttpClient(registry), "Acme.Core", "11.4.0", default)).Should().BeTrue();
        (await PublishedVersions.ExistsAsync(new HttpClient(registry), "Acme.Core", "11.5.0", default)).Should().BeFalse();
        registry.Asked.Should().AllSatisfy(u => u.Should().Be("https://api.nuget.org/v3-flatcontainer/acme.core/index.json"));
    }

    [Fact]
    public async Task A_scoped_npm_name_is_asked_on_npm_only_and_a_failing_registry_is_no_answer()
    {
        var registry = new Registry(_ => Json("""{ "versions": { "22.1.0": {} } }"""));
        (await PublishedVersions.ExistsAsync(new HttpClient(registry), "@acme/ng", "22.1.0", default)).Should().BeTrue();
        registry.Asked.Should().Equal("https://registry.npmjs.org/@acme%2Fng");

        var down = new Registry(_ => new(System.Net.HttpStatusCode.BadGateway));
        (await PublishedVersions.ExistsAsync(new HttpClient(down), "@acme/ng", "22.2.0", default)).HasValue.Should().BeFalse("a failing registry is no answer");
    }

    [Fact]
    public void A_named_older_commit_does_not_call_newer_green_commits_not_passed()
    {
        IReadOnlyList<ForgeWorkflowRun> runs =
        [
            Run(3, "cccc000000000000000000000000000000000000", "success"),
            Run(2, "bbbb000000000000000000000000000000000000", "success"),
        ];
        var result = new GitWriterPromoteResult(GitWriterPromoteResult.Promoted, null, [], null, 10);

        ReleaseRecipient.Report(new Repository { FullName = "acme/widgets" }, "master", "release.yml", runs[1], runs, result)
            .Should().NotContain("left out");
    }
}
