using System.Text;
using CodeCoverage.Git;
using CodeCoverage.GitWriter.Jobs;
using CodeCoverage.GitWriter.Tests._Infrastructure;

namespace CodeCoverage.GitWriter.Tests;

/// <summary>
/// Conflict resolution v2 in the git writer (docs/coverage_merge_queue_PRD.md §10, O35 #4, O36): a keep-both rule on the
/// wire, the prepare's resolutions and resolver version, the same commit from prepare, queue-branch push and publish,
/// post-apply validation, and the patches of several regenerate workflows.
/// </summary>
public class ResolutionV2Tests : WriterTestBase
{
    private const string Security = "apps/DemoApp/DemoApp/App_Data/security.json";

    private static object[] KeepBothRules(params string[] validate)
        => [new { path = "**/security.json", strategy = "keep-both", validate, regenerate = false }];

    /// <summary>main and the pull request each insert one right into the same gap of <c>security.json</c>.</summary>
    private static string SameGapInsertions(Origin origin)
    {
        origin.Checkout("main").Write(Security, "{\n  \"rights\": [\n    \"A\",\n    \"C\"\n  ]\n}\n");
        origin.Commit("main: security.json");
        origin.Push("main");
        FixtureGit.Run(origin.Work, "checkout", "--quiet", "-B", "feature", "main");
        origin.Write(Security, "{\n  \"rights\": [\n    \"A\",\n    \"B-pr\",\n    \"C\"\n  ]\n}\n");
        var head = origin.Commit("pr: a right");
        origin.Push("feature").PullRequest(1, "feature");
        origin.Checkout("main").Write(Security, "{\n  \"rights\": [\n    \"A\",\n    \"B-main\",\n    \"C\"\n  ]\n}\n");
        origin.Commit("main: another right");
        origin.Push("main");
        return head;
    }

    /// <summary>§10.4 step 5, §0 D65: keep-both is a pure function of the blobs, so the prepare, the queue-branch push and
    /// the publish all build the one commit; the prepare says how it resolved and under which resolver.</summary>
    [Fact]
    public async Task A_keep_both_resolution_is_rebuilt_identically_by_the_queue_branch_push_and_the_publish()
    {
        var origin = Seed();
        var head = SameGapInsertions(origin);
        var rules = KeepBothRules("json-no-duplicate-keys");

        var response = await Writer.PrepareAsync(PrepareRequest(origin, head, autoResolve: rules));

        response.Outcome.Should().Be("prepared", response.Detail);
        response.ResolvedPaths.Should().Equal([Security]);
        response.Resolutions.Should().Equal([new ResolutionWire(Security, "keep-both", false, 0)]);
        response.ResolverVersion.Should().StartWith("resolution-2; git version");
        var prepared = new Prepared(response.PreparedSha!, head, response.BaseSha!);

        var pushed = await Writer.PushQueueBranchAsync(With(QueueBranchRequest(origin, prepared), new() { ["autoResolve"] = rules }));
        pushed.Outcome.Should().Be("pushed", pushed.Detail);
        pushed.PreparedSha.Should().Be(prepared.Sha);

        var published = await Writer.PublishAsync(With(PublishRequest(origin, prepared), new() { ["autoResolve"] = rules }));
        published.Outcome.Should().Be("published", published.Detail);
        published.FinalSha.Should().Be(prepared.Sha);
        origin.InBare("cat-file", "blob", $"{prepared.Sha}:{Security}")
            .Should().Be("{\n  \"rights\": [\n    \"A\",\n    \"B-main\",\n    \"B-pr\",\n    \"C\"\n  ]\n}\n");
    }

    /// <summary>§10.4: a patched path whose rule names validators is judged after the apply; a regenerate step that writes
    /// broken JSON is refused as <c>invalid-content</c>, naming the validator, never the content.</summary>
    [Fact]
    public async Task A_patch_that_leaves_a_validated_file_invalid_is_rejected_as_invalid_content()
    {
        var origin = Seed();
        var head = RollUpConflict(origin);
        object[] rules = [new { path = "**/App_Data/modelHashes.json", keys = new[] { "modelHash", "contextRoots", "modelFiles" }, validate = new[] { "json" } }];
        var response = await Writer.PrepareAsync(PrepareRequest(origin, head, autoResolve: rules));
        response.Outcome.Should().Be("prepared", response.Detail);
        var prepared = new Prepared(response.PreparedSha!, head, response.BaseSha!);
        (await Writer.PushQueueBranchAsync(With(QueueBranchRequest(origin, prepared), new() { ["autoResolve"] = rules }))).Outcome.Should().Be("pushed");

        FixtureGit.Run(origin.Work, "fetch", "--quiet", "origin", QueueBranch);
        var broken = ModelHashes.Render("regenerated", [.. ModelHashes.BaseEntities, "Customer", "Order"]).Replace("  }\n}\n", "  },\n}\n");
        origin.Checkout("regen", prepared.Sha).Write(ModelHashes.Path, broken);
        origin.Commit("regenerate, badly");
        var patch = FixtureGit.Run(origin.Work, "diff", "--binary", prepared.Sha, "HEAD");
        origin.Checkout("main");

        var published = await Writer.PublishAsync(With(PublishRequest(origin, prepared, patch), new() { ["autoResolve"] = rules }));

        published.Outcome.Should().Be("patch-rejected", published.Detail);
        published.Reason.Should().Be(PatchReasons.InvalidContent);
        published.Paths.Should().Equal([ModelHashes.Path]);
        published.Detail.Should().Contain("json");
        origin.Ref("refs/heads/feature").Should().Be(head, "nothing was published");

        // Control: without the validator the same resolution builds the same prepared commit, and the same patch publishes.
        var control = await Writer.PublishAsync(With(PublishRequest(origin, prepared, patch), new() { ["autoResolve"] = new object[] { new { path = "**/App_Data/modelHashes.json", keys = new[] { "modelHash", "contextRoots", "modelFiles" } } } }));
        control.Outcome.Should().Be("published", control.Detail);
    }

    private static readonly IReadOnlyList<PathGlob> AnyPath = [new PathGlob("**")];

    private static byte[] Edit(string path, string from, string to)
        => Encoding.UTF8.GetBytes($"diff --git a/{path} b/{path}\nindex 1111111..2222222 100644\n--- a/{path}\n+++ b/{path}\n@@ -1 +1 @@\n-{from}\n+{to}\n");

    [Fact]
    public void Patches_on_different_paths_combine_in_the_order_given()
    {
        var (patch, verdict) = PatchCombiner.Combine([Edit("b.txt", "old", "new"), Edit("a.txt", "old", "new")], AnyPath, 1024 * 1024);

        verdict.Accepted.Should().BeTrue(verdict.Detail);
        verdict.Paths.Should().Equal(["a.txt", "b.txt"]);
        var text = Encoding.UTF8.GetString(patch!);
        text.IndexOf("a/b.txt", StringComparison.Ordinal).Should().BeLessThan(text.IndexOf("a/a.txt", StringComparison.Ordinal));
        PatchValidator.Validate(patch, AnyPath, 1024 * 1024).Accepted.Should().BeTrue("the combined patch is a patch the writer accepts");
    }

    [Fact]
    public void Two_patches_that_change_one_path_differently_overlap_and_identical_sections_are_kept_once()
    {
        var (overlap, refused) = PatchCombiner.Combine([Edit("a.txt", "old", "one"), Edit("a.txt", "old", "two")], AnyPath, 1024 * 1024);
        overlap.Should().BeNull();
        refused.Reason.Should().Be(PatchReasons.Overlap);
        refused.Paths.Should().Equal(["a.txt"]);

        var (same, kept) = PatchCombiner.Combine([Edit("a.txt", "old", "one"), Edit("a.txt", "old", "one")], AnyPath, 1024 * 1024);
        kept.Accepted.Should().BeTrue();
        same.Should().Equal(Edit("a.txt", "old", "one"));
    }

    [Fact]
    public void Each_patch_is_validated_and_their_total_is_capped()
    {
        PatchCombiner.Combine([Edit("a.txt", "old", "new"), Edit(".github/workflows/x.yml", "a", "b")], AnyPath, 1024 * 1024).Verdict.Reason
            .Should().Be(PatchReasons.UnsafePath);
        PatchCombiner.Combine([Edit("a.txt", "old", "new"), Edit("b.txt", "old", "new")], AnyPath, 100).Verdict.Reason
            .Should().Be(PatchReasons.TooLarge);
        PatchCombiner.Combine([[], []], AnyPath, 100).Patch.Should().BeEmpty("runs that changed nothing make no patch");
    }
}
