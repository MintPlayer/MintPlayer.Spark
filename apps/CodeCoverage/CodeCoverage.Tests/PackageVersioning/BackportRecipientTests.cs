using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using CodeCoverage.MergeQueue;
using CodeCoverage.PackageVersioning;
using CodeCoverage.Tests.MergeQueue;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests.PackageVersioning;

/// <summary>
/// #153 M10 (D10): who may ask for a backport, and that a line naming no release branch refuses the whole request
/// before the writer is reached.
/// </summary>
public class BackportRecipientTests : CoverageRavenTest
{
    private const string RepositoryId = "Repositories/153";
    private static readonly string Merge = new('c', 40);

    private readonly FakeForgeVersioning forge = new()
    {
        PullRequest = new(7, "fix: guard", null, "fix", new string('h', 40), "main", false, true, false, Merge, []),
    };
    private readonly IForgeMergeQueue git = Substitute.For<IForgeMergeQueue>();
    private readonly IForgeIntegration integration = Substitute.For<IForgeIntegration>();
    private readonly IGitWriterSidecar writer = Substitute.For<IGitWriterSidecar>();
    private readonly IBackportRegenerationEngine regenerations = Substitute.For<IBackportRegenerationEngine>();
    private readonly FakeOutbox outbox = new();

    public BackportRecipientTests()
    {
        git.Provider.Returns(EForgeProvider.GitHub);
        integration.Provider.Returns(EForgeProvider.GitHub);
        integration.GetFileContentAsync(Arg.Any<Repository>(), "main", CoverageYml.FileName, Arg.Any<CancellationToken>()).Returns("versioning:\n");
        // NSubstitute answers an unconfigured Task<string?> with "", not null, which would make every branch exist.
        git.GetBranchHeadAsync(Arg.Any<Repository>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        git.GetBranchHeadAsync(Arg.Any<Repository>(), "release/8.x", Arg.Any<CancellationToken>()).Returns(new string('8', 40));
        git.GetActorRoleAsync(Arg.Any<Repository>(), "maintainer", Arg.Any<CancellationToken>()).Returns(EForgeRepositoryRole.Write);
        git.GetActorRoleAsync(Arg.Any<Repository>(), "visitor", Arg.Any<CancellationToken>()).Returns(EForgeRepositoryRole.Triage);
        git.GetPullRequestCommitsAsync(Arg.Any<Repository>(), 7, Arg.Any<CancellationToken>()).Returns([]);
        git.GetWriterAccessAsync(Arg.Any<Repository>(), null, false, Arg.Any<CancellationToken>())
            .Returns(new ForgePushAccess(new ForgeWriterAccess("https://example.invalid/acme/widgets.git", "https://example.invalid/acme/widgets.git", "x", "y"), null));
    }

    private Task HandleAsync(IDocumentStore store, string actor, EBackportSource source, params string[] lines)
        => HandleAsync(store, actor, source, false, lines);

    private async Task HandleAsync(IDocumentStore store, string actor, EBackportSource source, bool draft, params string[] lines)
    {
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new Repository { FullName = "acme/widgets", Provider = EForgeProvider.GitHub, DefaultBranch = "main" }, RepositoryId);
            await seed.SaveChangesAsync();
        }
        using var session = store.OpenAsyncSession();
        await new BackportRecipient(session, new SingleForgeResolver(integration), [forge], [git], writer, regenerations, outbox, NullLogger<BackportRecipient>.Instance)
            .HandleAsync(new BackportMessage { RepositoryId = RepositoryId, Number = 7, Lines = lines, ActorLogin = actor, Source = source, Draft = draft });
    }

    [Fact]
    public async Task A_line_naming_no_release_branch_refuses_the_whole_request()
    {
        using var store = GetDocumentStore();

        await HandleAsync(store, "maintainer", EBackportSource.Comment, "8.x", "9.x");

        forge.Comments.Single().Body.Should().Contain("`9.x` names no release branch");
        await writer.DidNotReceiveWithAnyArgs().VersioningBackportAsync(default!, default);
    }

    private void WriterAnswers(GitWriterBackportResult result)
        => writer.VersioningBackportAsync(Arg.Any<GitWriterBackportRequest>(), Arg.Any<CancellationToken>()).Returns(result);

    private static GitWriterBackportTargetResult Target(string push, string? detail = null)
        => new("release/8.x", "backport/7-8.x", new string('d', 40), push, [], detail);

    [Fact]
    public async Task A_requested_backport_is_queued_on_the_versioning_lane()
    {
        var bus = new RecordingMessageBus();

        await new BackportEventsRecipient(bus).HandleAsync(new ForgeWebhookMessage<BackportRequested>(EForgeProvider.GitHub,
            new BackportRequested(RepositoryId, 7, ["8.x"], "maintainer", EBackportSource.Label)));

        bus.Of<BackportMessage>().Should().ContainSingle().Which.Should().BeEquivalentTo(new BackportMessage
        {
            RepositoryId = RepositoryId, Number = 7, Lines = ["8.x"], ActorLogin = "maintainer", Source = EBackportSource.Label,
        });
    }

    [Theory]
    [InlineData(EBackportSource.Comment, true)]
    [InlineData(EBackportSource.Label, false)]
    public async Task An_unmerged_pull_request_is_told_to_use_the_label_only_when_asked_by_comment(EBackportSource source, bool commented)
    {
        using var store = GetDocumentStore();
        forge.PullRequest = forge.PullRequest! with { Merged = false, MergeCommitSha = null };

        await HandleAsync(store, "maintainer", source, "8.x");

        forge.Comments.Select(c => c.Body).Should().HaveCount(commented ? 1 : 0);
        if (commented) forge.Comments.Single().Body.Should().Contain("Add the label `backport: 8.x`");
        await writer.DidNotReceiveWithAnyArgs().VersioningBackportAsync(default!, default);
    }

    [Fact]
    public async Task A_repository_without_a_versioning_section_has_nothing_to_backport_to()
    {
        using var store = GetDocumentStore();
        integration.GetFileContentAsync(Arg.Any<Repository>(), "main", CoverageYml.FileName, Arg.Any<CancellationToken>()).Returns("coverage:\n  minimum: 80\n");

        await HandleAsync(store, "maintainer", EBackportSource.Comment, "8.x");

        forge.Comments.Single().Body.Should().Contain("has no versioning section");
    }

    public static TheoryData<string, Exception?> WriterUnavailable => new()
    {
        { "no-access", null },
        { "refused", new GitWriterRefusedException("versioning/backport", "commit", "a sha") },
        { "down", new HttpRequestException("Connection refused") },
        { "timeout", new TaskCanceledException("The request timed out") },
    };

    [Theory]
    [MemberData(nameof(WriterUnavailable))]
    public async Task A_writer_that_cannot_run_the_backport_says_so_and_ends_the_credential(string why, Exception? thrown)
    {
        using var store = GetDocumentStore();
        if (why == "no-access")
            git.GetWriterAccessAsync(Arg.Any<Repository>(), null, false, Arg.Any<CancellationToken>()).Returns(new ForgePushAccess(null, "App not installed"));
        else
            writer.VersioningBackportAsync(Arg.Any<GitWriterBackportRequest>(), Arg.Any<CancellationToken>()).Returns<GitWriterBackportResult>(_ => throw thrown!);

        await HandleAsync(store, "maintainer", EBackportSource.Label, "8.x");

        forge.Comments.Single().Body.Should().Contain("could not run").And.Contain("add the label again");
        await git.Received(why == "no-access" ? 0 : 1).RevokeWriterAccessAsync(Arg.Any<Repository>(), Arg.Any<ForgeWriterAccess>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_conflict_names_the_files_and_the_cherry_pick_and_a_failure_its_reason()
    {
        using var store = GetDocumentStore();
        WriterAnswers(new GitWriterBackportResult(GitWriterBackportResult.Backported, null, "squash", null,
            [new("release/8.x", "backport/7-8.x", null, GitWriterBackportTargetResult.Conflicted, ["src/Guard.cs"])], null, 1));

        await HandleAsync(store, "maintainer", EBackportSource.Label, "8.x");

        forge.Comments.Single().Body.Should().Contain("- `release/8.x`: conflicts; nothing was pushed (see below).")
            .And.Contain("`src/Guard.cs`").And.Contain($"git cherry-pick -x {Merge}\n");
        forge.OpenedPullRequests.Should().BeEmpty();

        WriterAnswers(new GitWriterBackportResult("failed", "clone-failed", null, null, null, "remote hung up", 1));
        await HandleAsync(store, "maintainer", EBackportSource.Label, "8.x");

        forge.Comments[1].Body.Should().Be("The backport failed (clone-failed). remote hung up");
    }

    [Fact]
    public async Task Each_line_reports_what_happened_to_it()
    {
        using var store = GetDocumentStore();
        foreach (var major in new[] { "7", "6" })
            git.GetBranchHeadAsync(Arg.Any<Repository>(), $"release/{major}.x", Arg.Any<CancellationToken>()).Returns(new string(major[0], 40));
        WriterAnswers(new GitWriterBackportResult(GitWriterBackportResult.Backported, null, "squash", null,
        [
            Target(GitWriterBackportTargetResult.Exists) with { Branch = "release/8.x" },
            Target(GitWriterBackportTargetResult.Empty) with { Branch = "release/7.x", Head = "backport/7-7.x" },
            Target("rejected", "protected branch") with { Branch = "release/6.x", Head = "backport/7-6.x" },
        ], null, 1));

        await HandleAsync(store, "maintainer", EBackportSource.Label, "8.x", "7.x", "6.x");

        forge.Comments.Single().Body.Should().Be(
            "Backports:\n\n"
            + "- `release/8.x`: `backport/7-8.x` exists already; nothing was pushed.\n"
            + "- `release/7.x`: already holds this change.\n"
            + "- `release/6.x`: the push was rejected. protected branch");
        forge.OpenedPullRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_raised_first_party_package_is_diagnosed_whatever_the_lines_outcome()
    {
        using var store = GetDocumentStore();
        WriterAnswers(new GitWriterBackportResult(GitWriterBackportResult.Backported, null, "squash", null,
            [Target(GitWriterBackportTargetResult.Empty) with { Raises = [new("package.json", "npm", "@acme/ui", "^9.1.0", "^9.2.0", "^8.4.0")] }], null, 1));

        await HandleAsync(store, "maintainer", EBackportSource.Label, "8.x");

        forge.Comments.Single().Body.Should().Be("Backports:\n\n- `release/8.x`: already holds this change.\n\n"
            + "`release/8.x` keeps `@acme/ui` at `^8.4.0`: the change raised it from `^9.1.0` to `^9.2.0` on `main`, above this line's major, "
            + "so the line probably needs what `9.2.0` brought, released on its own major (8).");
    }

    [Fact]
    public async Task The_commit_subjects_sent_are_one_line_each_and_bounded()
    {
        using var store = GetDocumentStore();
        git.GetPullRequestCommitsAsync(Arg.Any<Repository>(), 7, Arg.Any<CancellationToken>()).Returns(
        [
            new ForgePullRequestCommit("a", null, null, null, false, "fix: guard\n\nThe body is not a subject."),
            new ForgePullRequestCommit("b", null, null, null, false, "   "),
            new ForgePullRequestCommit("c", null, null, null, false, new string('x', 300)),
        ]);
        WriterAnswers(new GitWriterBackportResult(GitWriterBackportResult.Backported, null, "squash", null, [], null, 1));

        await HandleAsync(store, "maintainer", EBackportSource.Label, "8.x");

        await writer.Received(1).VersioningBackportAsync(
            Arg.Is<GitWriterBackportRequest>(r => r.Subjects.SequenceEqual(new[] { "fix: guard", "(no subject)", new string('x', 256) })),
            Arg.Any<CancellationToken>());
        forge.Comments.Should().BeEmpty("a backport with no targets to report says nothing");
    }

    public static TheoryData<Exception> ForgeFailures => new()
    {
        new ForgeAccessDeniedException("Resource not accessible by integration", "pull_requests=write"),
        new ForgeRequestException("GitHub answered 502", 502),
    };

    [Theory]
    [MemberData(nameof(ForgeFailures))]
    public async Task A_forge_that_refuses_or_fails_is_logged_not_thrown(Exception thrown)
    {
        using var store = GetDocumentStore();
        git.GetActorRoleAsync(Arg.Any<Repository>(), "maintainer", Arg.Any<CancellationToken>()).Returns<EForgeRepositoryRole>(_ => throw thrown);

        await HandleAsync(store, "maintainer", EBackportSource.Comment, "8.x");

        forge.Comments.Should().BeEmpty();
    }

    [Fact]
    public async Task A_label_or_comment_needs_write_access_but_a_merge_does_not()
    {
        using var store = GetDocumentStore();
        writer.VersioningBackportAsync(Arg.Any<GitWriterBackportRequest>(), Arg.Any<CancellationToken>()).Returns(new GitWriterBackportResult(
            GitWriterBackportResult.Backported, null, "squash", null,
            [new("release/8.x", "backport/7-8.x", new string('d', 40), GitWriterBackportTargetResult.Created, [])], null, 1));

        await HandleAsync(store, "visitor", EBackportSource.Label, "8.x");
        forge.Comments.Single().Body.Should().Contain("needs write access");
        await writer.DidNotReceiveWithAnyArgs().VersioningBackportAsync(default!, default);

        await HandleAsync(store, "visitor", EBackportSource.Merge, "8.x");
        await writer.Received(1).VersioningBackportAsync(
            Arg.Is<GitWriterBackportRequest>(r => r.Commit == Merge && r.SourceBranch == "main" && r.Targets.Single().Branch == "release/8.x" && r.Targets.Single().Head == "backport/7-8.x"),
            Arg.Any<CancellationToken>());
        forge.OpenedPullRequests.Should().Equal(("backport/7-8.x", "release/8.x", "[8.x] fix: guard",
            $"Backport of #7 to `release/8.x`.\n\nCherry-picked from {Merge}. CI on this pull request is the check against the line's platform.\n\n" + Marker7));
    }

    /// <summary>The provenance marker every backport body ends with (#570 M8c).</summary>
    private static readonly string Marker7 = BackportProvenance.Marker("acme/widgets", 7);

    /// <summary>A queue section that reads clean: take-base needs a regenerate workflow, and no keep-both defaults.</summary>
    private const string QueueYml = "versioning:\nmergeQueue:\n  postRebaseActionName: regenerate.yml\n  keepBothDefaults: false\n  autoResolve:\n";

    [Theory]
    [InlineData(QueueYml + "    - path: \"**/App_Data/modelHashes.json\"\n", 1)]
    [InlineData(QueueYml + "    - path: \"**/App_Data/modelHashes.json\"\n      strategy: guess\n", 0)]
    [InlineData("versioning:\n", 0)]
    public void The_rules_come_from_the_default_branch_and_a_broken_section_gives_none(string yml, int count)
    {
        // #570 M5: merge-queue O16 holds for lines too; a section with errors resolves nothing.
        var rules = BackportRecipient.AutoResolveRules(yml, "main");

        rules.Should().HaveCount(count);
        if (count == 1)
            (rules.Single().Path, rules.Single().AnyLine).Should().Be(("**/App_Data/modelHashes.json", true));
    }

    [Fact]
    public async Task The_default_branch_rules_go_with_every_target_and_resolutions_are_listed()
    {
        using var store = GetDocumentStore();
        integration.GetFileContentAsync(Arg.Any<Repository>(), "main", CoverageYml.FileName, Arg.Any<CancellationToken>())
            .Returns(QueueYml + "    - path: \"**/packages.lock.json\"\n      strategy: take-base\n");
        WriterAnswers(new GitWriterBackportResult(GitWriterBackportResult.Backported, null, "squash", null,
            [Target(GitWriterBackportTargetResult.Created) with { Resolutions = [new("App/packages.lock.json", "take-base", true, 0)] }], null, 1));

        await HandleAsync(store, "maintainer", EBackportSource.Label, "8.x");

        await writer.Received(1).VersioningBackportAsync(
            Arg.Is<GitWriterBackportRequest>(r => r.Targets.Single().AutoResolve!.Single().Path == "**/packages.lock.json"), Arg.Any<CancellationToken>());
        forge.OpenedPullRequests.Single().Body.Should().EndWith(
            "Conflicts resolved by the `mergeQueue.autoResolve` rules (the line's side is stage 2):\n- `App/packages.lock.json`: take-base, needs regenerating\n\n" + Marker7);
        forge.Comments.Single().Body.Should().Contain(", with 1 conflict resolved automatically");
    }

    [Fact]
    public async Task A_conflicting_line_still_lets_the_clean_line_open_its_pull_request()
    {
        // #570 D6: lines are independent.
        using var store = GetDocumentStore();
        git.GetBranchHeadAsync(Arg.Any<Repository>(), "release/7.x", Arg.Any<CancellationToken>()).Returns(new string('7', 40));
        WriterAnswers(new GitWriterBackportResult(GitWriterBackportResult.Backported, null, "squash", null,
        [
            Target(GitWriterBackportTargetResult.Created) with
            {
                PlatformChanges = [new("global.json", "global.json sdk", "10.0.100", "10.0.200")],
            },
            new("release/7.x", "backport/7-7.x", null, GitWriterBackportTargetResult.Conflicted, ["src/Guard.cs"]),
        ], null, 1));

        await HandleAsync(store, "maintainer", EBackportSource.Label, "8.x", "7.x");

        forge.OpenedPullRequests.Should().ContainSingle().Which.Body.Should()
            .EndWith("The change edits this line's platform:\n- `global.json`: global.json sdk `10.0.100` → `10.0.200`\n\n" + Marker7);
        var body = forge.Comments.Single().Body;
        body.Should().StartWith("Backports:\n\n- `release/8.x`: #").And.Contain("- `release/7.x`: conflicts; nothing was pushed (see below).")
            .And.Contain("`release/7.x` conflicts on:\n- `src/Guard.cs`");
    }

    [Fact]
    public async Task The_backport_section_sends_draft_and_markers_and_a_drafted_line_opens_a_draft()
    {
        // #570 M5, PRD §5.8: D7 onConflict draft and D8 markers, from the default branch.
        using var store = GetDocumentStore();
        integration.GetFileContentAsync(Arg.Any<Repository>(), "main", CoverageYml.FileName, Arg.Any<CancellationToken>())
            .Returns("versioning:\nbackport:\n  onConflict: draft\n  markers:\n    disable: [setup-action]\n    packages: [\"@mintplayer/*\"]\n");
        WriterAnswers(new GitWriterBackportResult(GitWriterBackportResult.Backported, null, "squash", null,
            [Target(GitWriterBackportTargetResult.Drafted) with { Conflicts = ["src/Guard.cs"] }], null, 1));

        await HandleAsync(store, "maintainer", EBackportSource.Label, "8.x");

        await writer.Received(1).VersioningBackportAsync(
            Arg.Is<GitWriterBackportRequest>(r => r.Targets.Single().Draft && r.Markers!.Disable.Single() == "setup-action" && r.Markers.Packages.Single() == "@mintplayer/*"),
            Arg.Any<CancellationToken>());
        forge.DraftHeads.Should().Equal("backport/7-8.x");
        forge.OpenedPullRequests.Single().Body.Should().Contain("**It conflicts**").And.EndWith("- `src/Guard.cs`\n\n" + Marker7);
        forge.Comments.Single().Body.Should().Contain("a draft with 1 conflicting file to resolve").And.NotContain("conflicts on:");
    }

    [Theory]   // T1: a /backport comment (with or without --draft) by anyone below write runs nothing
    [InlineData(EForgeRepositoryRole.None, false)]
    [InlineData(EForgeRepositoryRole.Read, true)]
    [InlineData(EForgeRepositoryRole.Triage, true)]
    public async Task A_backport_comment_without_write_access_runs_nothing(EForgeRepositoryRole role, bool draft)
    {
        using var store = GetDocumentStore();
        git.GetActorRoleAsync(Arg.Any<Repository>(), "outsider", Arg.Any<CancellationToken>()).Returns(role);

        await HandleAsync(store, "outsider", EBackportSource.Comment, draft, "8.x");

        forge.Comments.Single().Body.Should().Contain("needs write access");
        await writer.DidNotReceiveWithAnyArgs().VersioningBackportAsync(default!, default);
        await git.DidNotReceiveWithAnyArgs().GetWriterAccessAsync(default!, default, default, default);
        forge.OpenedPullRequests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true, true)]     // #570 M8d, D7: `/backport <line> --draft` drafts this request under onConflict: comment
    [InlineData(false, false)]   // the default stays a comment
    public async Task A_draft_request_drafts_its_lines_under_the_comment_default(bool asked, bool drafted)
    {
        using var store = GetDocumentStore();
        WriterAnswers(new GitWriterBackportResult(GitWriterBackportResult.Backported, null, "squash", null, [Target(GitWriterBackportTargetResult.Created)], null, 1));

        await HandleAsync(store, "maintainer", EBackportSource.Comment, asked, "8.x");

        await writer.Received(1).VersioningBackportAsync(Arg.Is<GitWriterBackportRequest>(r => r.Targets.Single().Draft == drafted), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_broken_backport_section_is_reported_and_its_defaults_apply()
    {
        using var store = GetDocumentStore();
        integration.GetFileContentAsync(Arg.Any<Repository>(), "main", CoverageYml.FileName, Arg.Any<CancellationToken>())
            .Returns("versioning:\nbackport:\n  onConflict: push\n");
        WriterAnswers(new GitWriterBackportResult(GitWriterBackportResult.Backported, null, "squash", null, [Target(GitWriterBackportTargetResult.Created)], null, 1));

        await HandleAsync(store, "maintainer", EBackportSource.Label, "8.x");

        await writer.Received(1).VersioningBackportAsync(
            Arg.Is<GitWriterBackportRequest>(r => !r.Targets.Single().Draft && r.Markers!.Packages.Count == 0), Arg.Any<CancellationToken>());
        forge.DraftHeads.Should().BeEmpty();
        forge.Comments.Single().Body.Should().Contain("has errors, so its defaults were used:\n- backport.onConflict 'push'");
    }

    [Fact]
    public async Task Regenerated_paths_go_with_every_target_and_a_line_that_needs_regenerating_waits_for_its_workflow()
    {
        // #570 M6, D9: the derived files (whole-file take-base rules that regenerate) keep the line's side; the pull request
        // opens after the line's workflow.
        using var store = GetDocumentStore();
        integration.GetFileContentAsync(Arg.Any<Repository>(), "main", CoverageYml.FileName, Arg.Any<CancellationToken>())
            .Returns(QueueYml + "    - path: \"**/packages.lock.json\"\n      strategy: take-base\n      regenerate: true\n      action: lock.yml\n");
        WriterAnswers(new GitWriterBackportResult(GitWriterBackportResult.Backported, null, "squash", null,
        [
            Target(GitWriterBackportTargetResult.Created) with
            {
                Resolutions = [new("App/packages.lock.json", "take-base", true, 0)],
                Regenerate = ["App/packages.lock.json", "app/package-lock.json"],
            },
        ], null, 1));

        await HandleAsync(store, "maintainer", EBackportSource.Label, "8.x");

        await writer.Received(1).VersioningBackportAsync(
            Arg.Is<GitWriterBackportRequest>(r => r.Targets.Single().RegeneratedPaths!.SequenceEqual(new[] { "**/packages.lock.json" })), Arg.Any<CancellationToken>());
        forge.OpenedPullRequests.Should().BeEmpty();
        await regenerations.Received(1).BeginAsync(Arg.Any<Repository>(), Arg.Is<BackportRegeneration>(r =>
            r.Head == "backport/7-8.x" && r.Branch == "release/8.x" && r.Number == 7 && r.Sha == new string('d', 40)
            && r.Workflows.SequenceEqual(new[] { "lock.yml", "regenerate.yml" })
            && r.Paths.Count == 2 && r.Title == "[8.x] fix: guard" && r.Body.StartsWith("Backport of #7 to `release/8.x`.")), Arg.Any<CancellationToken>());
        forge.Comments.Single().Body.Should().Contain("- `release/8.x`: `backport/7-8.x` is pushed; its pull request opens once `lock.yml`, `regenerate.yml` regenerated 2 files on the line");
    }

    [Fact]
    public async Task Hand_written_files_in_regeneratedPaths_merge_as_text_and_only_derived_files_keep_the_line_side()
    {
        // Owner, 2026-11-07: #580's backports reset Spark's Model/*.json and translations.json to the line's version, because
        // they are in regeneratedPaths, and lost the change's hand-written edits. Only the take-base files are reset now.
        using var store = GetDocumentStore();
        integration.GetFileContentAsync(Arg.Any<Repository>(), "main", CoverageYml.FileName, Arg.Any<CancellationToken>())
            .Returns(QueueYml
                + "    - path: \"**/App_Data/modelHashes.json\"\n      strategy: take-base\n      regenerate: true\n"
                + "    - path: \"**/App_Data/Model/*.json\"\n      strategy: keep-both\n      regenerate: true\n"
                + "    - path: \"**/settings.json\"\n      strategy: take-base\n      keys: [version]\n      regenerate: true\n"
                + "  regeneratedPaths:\n    - \"**/App_Data/modelHashes.json\"\n    - \"**/App_Data/Model/*.json\"\n"
                + "    - \"**/App_Data/translations.json\"\n    - \"**/settings.json\"\n");
        WriterAnswers(new GitWriterBackportResult(GitWriterBackportResult.Backported, null, "squash", null, [Target(GitWriterBackportTargetResult.Created)], null, 1));

        await HandleAsync(store, "maintainer", EBackportSource.Label, "8.x");

        await writer.Received(1).VersioningBackportAsync(
            Arg.Is<GitWriterBackportRequest>(r => r.Targets.Single().RegeneratedPaths!.SequenceEqual(new[] { "**/App_Data/modelHashes.json" })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_line_that_cannot_regenerate_opens_its_pull_request_with_a_note()
    {
        // #570 D9: a regeneration that cannot run never holds the backport back; the pull request says what is stale.
        using var store = GetDocumentStore();
        integration.GetFileContentAsync(Arg.Any<Repository>(), "main", CoverageYml.FileName, Arg.Any<CancellationToken>())
            .Returns("versioning:\nmergeQueue:\n  keepBothDefaults: false\n");
        WriterAnswers(new GitWriterBackportResult(GitWriterBackportResult.Backported, null, "squash", null,
            [Target(GitWriterBackportTargetResult.Created) with { Regenerate = ["app/package-lock.json"] }], null, 1));

        await HandleAsync(store, "maintainer", EBackportSource.Label, "8.x");

        await regenerations.DidNotReceiveWithAnyArgs().BeginAsync(default!, default!, default);
        forge.OpenedPullRequests.Single().Body.Should().EndWith(
            "**Not regenerated**: `mergeQueue.postRebaseActionName` names no regenerate workflow. These files kept the line's version and need regenerating on this branch:\n- `app/package-lock.json`\n");
    }

    [Fact]
    public async Task Under_postRebaseWhen_never_no_regenerated_paths_are_sent()
    {
        using var store = GetDocumentStore();
        integration.GetFileContentAsync(Arg.Any<Repository>(), "main", CoverageYml.FileName, Arg.Any<CancellationToken>())
            .Returns("versioning:\nmergeQueue:\n  postRebaseActionName: regenerate.yml\n  postRebaseWhen: never\n");
        WriterAnswers(new GitWriterBackportResult(GitWriterBackportResult.Backported, null, "squash", null, [Target(GitWriterBackportTargetResult.Created)], null, 1));

        await HandleAsync(store, "maintainer", EBackportSource.Label, "8.x");

        await writer.Received(1).VersioningBackportAsync(Arg.Is<GitWriterBackportRequest>(r => r.Targets.Single().RegeneratedPaths == null), Arg.Any<CancellationToken>());
    }

    // ── #570 M7 (D12): backport pull requests go through the merge queue, automatically ─────────────

    private const string EnabledQueue = "versioning:\nmergeQueue:\n  enabled: true\n  keepBothDefaults: false\n  branches: [main, \"release/*\"]\n";

    [Fact]
    public async Task A_backport_into_a_queue_line_is_handed_to_the_merge_queue()
    {
        using var store = GetDocumentStore();
        integration.GetFileContentAsync(Arg.Any<Repository>(), "main", CoverageYml.FileName, Arg.Any<CancellationToken>()).Returns(EnabledQueue);
        WriterAnswers(new GitWriterBackportResult(GitWriterBackportResult.Backported, null, "squash", null, [Target(GitWriterBackportTargetResult.Created)], null, 1));

        await HandleAsync(store, "maintainer", EBackportSource.Label, "8.x");

        var signal = outbox.Messages.Select(m => m.Message).OfType<MergeQueueSignalMessage>().Single();
        (signal.RepositoryId, signal.Kind, signal.Number, signal.Command, signal.Source, signal.Actor)
            .Should().Be((RepositoryId, EMergeQueueSignal.Command, 1000, EMergeQueueCommand.Enqueue, EMergeQueueCommandSource.Backports, BackportRegenerationEngine.QueueActor));
        forge.Comments.Single().Body.Should().Contain("- `release/8.x`: #1000, handed to the merge queue");
    }

    [Theory]
    [InlineData(EnabledQueue + "backport:\n  enqueue: false\n", GitWriterBackportTargetResult.Created)]
    [InlineData("versioning:\nmergeQueue:\n  enabled: false\n  branches: [main, \"release/*\"]\n", GitWriterBackportTargetResult.Created)]
    [InlineData("versioning:\nmergeQueue:\n  enabled: true\n  keepBothDefaults: false\n  branches: [main]\n", GitWriterBackportTargetResult.Created)]
    [InlineData(EnabledQueue, GitWriterBackportTargetResult.Drafted)]
    public async Task A_backport_is_not_enqueued_when_turned_off_outside_the_queue_or_a_draft(string yml, string push)
    {
        using var store = GetDocumentStore();
        integration.GetFileContentAsync(Arg.Any<Repository>(), "main", CoverageYml.FileName, Arg.Any<CancellationToken>()).Returns(yml);
        WriterAnswers(new GitWriterBackportResult(GitWriterBackportResult.Backported, null, "squash", null, [Target(push) with { Conflicts = ["src/Guard.cs"] }], null, 1));

        await HandleAsync(store, "maintainer", EBackportSource.Label, "8.x");

        outbox.Messages.Select(m => m.Message).OfType<MergeQueueSignalMessage>().Should().BeEmpty();
        forge.OpenedPullRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task A_backport_with_stale_regenerated_files_is_not_enqueued()
    {
        using var store = GetDocumentStore();
        integration.GetFileContentAsync(Arg.Any<Repository>(), "main", CoverageYml.FileName, Arg.Any<CancellationToken>()).Returns(EnabledQueue);
        WriterAnswers(new GitWriterBackportResult(GitWriterBackportResult.Backported, null, "squash", null,
            [Target(GitWriterBackportTargetResult.Created) with { Regenerate = ["app/package-lock.json"] }], null, 1));

        await HandleAsync(store, "maintainer", EBackportSource.Label, "8.x");

        outbox.Messages.Select(m => m.Message).OfType<MergeQueueSignalMessage>().Should().BeEmpty();
        forge.OpenedPullRequests.Single().Body.Should().Contain("**Not regenerated**");
    }

    [Fact]
    public async Task A_line_that_regenerates_first_carries_the_enqueue_decision_to_its_saga()
    {
        using var store = GetDocumentStore();
        integration.GetFileContentAsync(Arg.Any<Repository>(), "main", CoverageYml.FileName, Arg.Any<CancellationToken>())
            .Returns(EnabledQueue + "  postRebaseActionName: regenerate.yml\n");
        WriterAnswers(new GitWriterBackportResult(GitWriterBackportResult.Backported, null, "squash", null,
            [Target(GitWriterBackportTargetResult.Created) with { Regenerate = ["app/package-lock.json"] }], null, 1));

        await HandleAsync(store, "maintainer", EBackportSource.Label, "8.x");

        await regenerations.Received(1).BeginAsync(Arg.Any<Repository>(), Arg.Is<BackportRegeneration>(r => r.Enqueue), Arg.Any<CancellationToken>());
        outbox.Messages.Should().BeEmpty("the saga enqueues once the pull request opens");
    }
}
