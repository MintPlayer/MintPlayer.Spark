using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using CodeCoverage.MergeQueue;
using CodeCoverage.PackageVersioning;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests.PackageVersioning;

/// <summary>#570 M8b, PRD D5, D16, D18: default backport labels when a pull request opens, and the "Backport targets" check.</summary>
public class BackportTargetsRecipientTests : CoverageRavenTest
{
    private const string RepositoryId = "Repositories/570";
    private static readonly string Head = new('h', 40);
    private const string Lines = "versioning:\n  lines: {}\n";

    private readonly FakeForgeVersioning forge = new();
    private readonly IForgeMergeQueue git = Substitute.For<IForgeMergeQueue>();
    private readonly IForgeLabels labels = Substitute.For<IForgeLabels>();
    private readonly IForgeIntegration integration = Substitute.For<IForgeIntegration>();
    private readonly IGitWriterSidecar writer = Substitute.For<IGitWriterSidecar>();

    public BackportTargetsRecipientTests()
    {
        git.Provider.Returns(EForgeProvider.GitHub);
        labels.Provider.Returns(EForgeProvider.GitHub);
        integration.Provider.Returns(EForgeProvider.GitHub);
        git.ListBranchesAsync(Arg.Any<Repository>(), "release/", Arg.Any<CancellationToken>())
            .Returns(["release/dotnet-10.x", "release/dotnet-11.x", "release/angular-22.x"]);
        git.GetWriterAccessAsync(Arg.Any<Repository>(), null, false, Arg.Any<CancellationToken>())
            .Returns(new ForgePushAccess(new ForgeWriterAccess("https://example.invalid/acme/widgets.git", "https://example.invalid/acme/widgets.git", "x", "y"), null));
        labels.ListPullRequestLabelsAsync(Arg.Any<Repository>(), 7, Arg.Any<CancellationToken>()).Returns([]);
        Touches("dotnet");
    }

    private void Yml(string yml)
        => integration.GetFileContentAsync(Arg.Any<Repository>(), "main", CoverageYml.FileName, Arg.Any<CancellationToken>()).Returns(yml);

    private void Touches(params string[] ecosystems)
        => writer.VersioningEcosystemsAsync(Arg.Any<GitWriterEcosystemsRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GitWriterEcosystemsResult(GitWriterEcosystemsResult.Planned, null, ecosystems, null, 1));

    private async Task HandleAsync(IDocumentStore store, bool opened = true, string title = "feat: guard")
    {
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new Repository { FullName = "acme/widgets", Provider = EForgeProvider.GitHub, DefaultBranch = "main" }, RepositoryId);
            await seed.SaveChangesAsync();
        }
        using var session = store.OpenAsyncSession();
        await new BackportTargetsRecipient(session, new SingleForgeResolver(integration), [forge], [git], [labels], writer, NullLogger<BackportTargetsRecipient>.Instance)
            .HandleAsync(new BackportTargetsMessage { RepositoryId = RepositoryId, Number = 7, HeadSha = Head, Title = title, Opened = opened });
    }

    [Fact]
    public async Task An_opened_pull_request_is_labelled_for_every_supported_line_it_changes_and_checked()
    {
        using var store = GetDocumentStore();
        Yml(Lines + "backport:\n  lines:\n    dotnet-10.x: keep\n");

        await HandleAsync(store);

        await labels.Received(1).AddLabelAsync(Arg.Any<Repository>(), 7, "backport: dotnet-11.x", Arg.Any<CancellationToken>());
        await labels.Received(1).AddLabelAsync(Arg.Any<Repository>(), 7, "backport: dotnet-10.x", Arg.Any<CancellationToken>());
        await labels.DidNotReceive().AddLabelAsync(Arg.Any<Repository>(), 7, "backport: angular-22.x", Arg.Any<CancellationToken>());
        forge.Checks.Should().ContainSingle().Which.Title.Should().Be("2 lines apply: dotnet-11.x, dotnet-10.x");
        await writer.Received(1).VersioningEcosystemsAsync(
            Arg.Is<GitWriterEcosystemsRequest>(r => r.BaseBranch == "main" && r.HeadRef == "refs/pull/7/head" && r.HeadSha == Head && r.Settings.Lines != null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Fixes_mode_labels_only_the_current_line_of_a_feature()
    {
        using var store = GetDocumentStore();
        Yml(Lines + "backport:\n  defaultLabels: fixes\n  lines:\n    dotnet-10.x: keep\n");

        await HandleAsync(store);

        await labels.Received(1).AddLabelAsync(Arg.Any<Repository>(), 7, "backport: dotnet-11.x", Arg.Any<CancellationToken>());
        await labels.DidNotReceive().AddLabelAsync(Arg.Any<Repository>(), 7, "backport: dotnet-10.x", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_push_or_a_pull_request_that_already_carries_a_backport_label_only_gets_the_check()
    {
        using var store = GetDocumentStore();
        Yml(Lines);

        await HandleAsync(store, opened: false);
        labels.ListPullRequestLabelsAsync(Arg.Any<Repository>(), 7, Arg.Any<CancellationToken>()).Returns(["backport: dotnet-11.x"]);
        await HandleAsync(store, opened: true);

        await labels.DidNotReceiveWithAnyArgs().AddLabelAsync(default!, default, default!, default);
        forge.Checks.Should().HaveCount(2);
        forge.Checks[1].Text.Should().Contain("| `dotnet-11.x` | `release/dotnet-11.x` | yes, the current line | yes | yes |");
    }

    [Fact]
    public async Task A_repository_without_release_lines_is_left_alone()
    {
        using var store = GetDocumentStore();
        Yml("versioning:\n  mode: server\n");

        await HandleAsync(store);

        await writer.DidNotReceiveWithAnyArgs().VersioningEcosystemsAsync(default!, default);
        forge.Checks.Should().BeEmpty();
    }

    [Fact]
    public async Task A_head_that_moved_on_waits_for_its_own_message()
    {
        using var store = GetDocumentStore();
        Yml(Lines);
        writer.VersioningEcosystemsAsync(Arg.Any<GitWriterEcosystemsRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GitWriterEcosystemsResult("head-mismatch", null, [], null, 1));

        await HandleAsync(store);

        forge.Checks.Should().BeEmpty();
        await labels.DidNotReceiveWithAnyArgs().AddLabelAsync(default!, default, default!, default);
    }

    [Theory]
    [InlineData("no-access")]
    [InlineData("failed")]
    [InlineData("refused")]
    [InlineData("down")]
    [InlineData("timeout")]
    public async Task A_writer_that_names_no_ecosystems_leaves_the_pull_request_alone(string trouble)
    {
        using var store = GetDocumentStore();
        Yml(Lines);
        var writes = writer.VersioningEcosystemsAsync(Arg.Any<GitWriterEcosystemsRequest>(), Arg.Any<CancellationToken>());
        switch (trouble)
        {
            case "no-access":
                git.GetWriterAccessAsync(Arg.Any<Repository>(), null, false, Arg.Any<CancellationToken>()).Returns(new ForgePushAccess(null, "App not installed"));
                break;
            case "failed": writes.Returns(new GitWriterEcosystemsResult("failed", "clone-failed", null, "remote hung up", 1)); break;
            case "refused": writes.Returns<GitWriterEcosystemsResult>(_ => throw new GitWriterRefusedException("versioning/ecosystems", "headSha", "a sha")); break;
            case "down": writes.Returns<GitWriterEcosystemsResult>(_ => throw new HttpRequestException("Connection refused")); break;
            case "timeout": writes.Returns<GitWriterEcosystemsResult>(_ => throw new TaskCanceledException("The request timed out")); break;
        }

        await HandleAsync(store);

        forge.Checks.Should().BeEmpty();
        await labels.DidNotReceiveWithAnyArgs().AddLabelAsync(default!, default, default!, default);
        // The credential is handed back whenever one was handed out.
        await git.Received(trouble == "no-access" ? 0 : 1).RevokeWriterAccessAsync(Arg.Any<Repository>(), Arg.Any<ForgeWriterAccess>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_forge_that_refuses_or_fails_is_logged_not_thrown(bool denied)
    {
        using var store = GetDocumentStore();
        Yml(Lines);
        labels.AddLabelAsync(Arg.Any<Repository>(), 7, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(denied ? new ForgeAccessDeniedException("403", "issues: write") : new ForgeRequestException("502", 502)));

        await HandleAsync(store);

        forge.Checks.Should().BeEmpty();
    }

    [Fact]
    public async Task A_rate_limit_is_left_to_the_message_bus()
    {
        using var store = GetDocumentStore();
        Yml(Lines);
        labels.AddLabelAsync(Arg.Any<Repository>(), 7, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new ForgeRateLimitedException(TimeSpan.FromMinutes(1), null, EForgeRateLimitKind.Secondary, "slow down")));

        var act = () => HandleAsync(store);

        await act.Should().ThrowAsync<ForgeRateLimitedException>();
    }

    [Fact]
    public async Task A_forge_without_labels_or_a_disconnected_repository_is_left_alone()
    {
        using var store = GetDocumentStore();
        Yml(Lines);
        labels.Provider.Returns(EForgeProvider.GitLab);
        await HandleAsync(store);

        labels.Provider.Returns(EForgeProvider.GitHub);
        using (var session = store.OpenAsyncSession())
        {
            (await session.LoadAsync<Repository>(RepositoryId)).Connection = RepositoryConnection.Disconnected;
            await session.SaveChangesAsync();
            await new BackportTargetsRecipient(session, new SingleForgeResolver(integration), [forge], [git], [labels], writer, NullLogger<BackportTargetsRecipient>.Instance)
                .HandleAsync(new BackportTargetsMessage { RepositoryId = RepositoryId, Number = 7, HeadSha = Head, Opened = true });
        }

        await writer.DidNotReceiveWithAnyArgs().VersioningEcosystemsAsync(default!, default);
        forge.Checks.Should().BeEmpty();
    }

    [Theory]
    [InlineData("human", true)]
    [InlineData("bot", false)]
    [InlineData("backport-head", false)]
    [InlineData("other-base", false)]
    [InlineData("archived", false)]
    [InlineData("unknown-repository", false)]
    public async Task Only_a_persons_pull_request_into_the_default_branch_is_forwarded(string kind, bool forwarded)
    {
        using var store = GetDocumentStore();
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new Repository { FullName = "acme/widgets", Provider = EForgeProvider.GitHub, DefaultBranch = "main", Archived = kind == "archived" }, RepositoryId);
            await seed.SaveChangesAsync();
        }
        var bus = new RecordingMessageBus();
        var evt = new PullRequestUpdated(kind == "unknown-repository" ? "Repositories/404" : RepositoryId, 7, Head,
            kind == "backport-head" ? "backport/6-dotnet-10.x" : "feature/guard", kind == "other-base" ? "release/dotnet-10.x" : "main", new string('b', 40),
            "fix: guard", IsFirstOpen: true, AuthorIsBot: kind == "bot");

        using var session = store.OpenAsyncSession();
        await new BackportTargetsEventsRecipient(session, bus).HandleAsync(new ForgeWebhookMessage<PullRequestUpdated>(EForgeProvider.GitHub, evt));

        if (forwarded)
            bus.Of<BackportTargetsMessage>().Should().ContainSingle().Which.Should().BeEquivalentTo(new BackportTargetsMessage
            {
                RepositoryId = RepositoryId, Number = 7, HeadSha = Head, Title = "fix: guard", Opened = true,
            });
        else
            bus.Messages.Should().BeEmpty();
    }
}
