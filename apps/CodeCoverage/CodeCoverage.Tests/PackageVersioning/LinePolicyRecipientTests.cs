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

/// <summary>Segment E (resilience PRD Q1b): the "Line policy" check on pull requests into release lines.</summary>
public class LinePolicyRecipientTests : CoverageRavenTest
{
    private const string RepositoryId = "Repositories/617";
    private static readonly string Head = new('h', 40);
    private const string Lines = "versioning:\n  lines: {}\n";

    private readonly FakeForgeVersioning forge = new();
    private readonly IForgeMergeQueue git = Substitute.For<IForgeMergeQueue>();
    private readonly IForgeIntegration integration = Substitute.For<IForgeIntegration>();

    public LinePolicyRecipientTests()
    {
        git.Provider.Returns(EForgeProvider.GitHub);
        integration.Provider.Returns(EForgeProvider.GitHub);
        git.ListBranchesAsync(Arg.Any<Repository>(), "release/", Arg.Any<CancellationToken>())
            .Returns(["release/dotnet-10.x", "release/dotnet-11.x", "release/angular-22.x"]);
        Yml(Lines);
    }

    private void Yml(string yml)
        => integration.GetFileContentAsync(Arg.Any<Repository>(), "main", CoverageYml.FileName, Arg.Any<CancellationToken>()).Returns(yml);

    private async Task HandleAsync(IDocumentStore store, string baseRef, string headRef = "fix/thing", string? recordedOnto = null)
    {
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new Repository { FullName = "acme/widgets", Provider = EForgeProvider.GitHub, DefaultBranch = "main" }, RepositoryId);
            if (recordedOnto is not null)
                await seed.StoreAsync(new BackportPullRequest { Branch = recordedOnto }, BackportPullRequest.DocumentId(RepositoryId, 7));
            await seed.SaveChangesAsync();
        }
        using var session = store.OpenAsyncSession();
        await new LinePolicyRecipient(session, new SingleForgeResolver(integration), [forge], [git], NullLogger<LinePolicyRecipient>.Instance)
            .HandleAsync(new LinePolicyMessage { RepositoryId = RepositoryId, Number = 7, HeadSha = Head, HeadRef = headRef, BaseRef = baseRef });
    }

    [Fact]
    public async Task A_persons_pull_request_into_the_current_line_fails()
    {
        using var store = GetDocumentStore();

        await HandleAsync(store, "release/dotnet-11.x");

        var check = forge.Checks.Should().ContainSingle().Which;
        check.Name.Should().Be("Line policy");
        check.Conclusion.Should().Be("failure");
        check.Summary.Should().Contain("against `main` instead");
    }

    [Fact]
    public async Task The_apps_backport_onto_the_current_line_passes()
    {
        using var store = GetDocumentStore();

        await HandleAsync(store, "release/dotnet-11.x", headRef: "backport/12-dotnet-11.x", recordedOnto: "release/dotnet-11.x");

        forge.Checks.Should().ContainSingle().Which.Conclusion.Should().Be("success");
    }

    [Fact]
    public async Task A_backport_record_for_another_line_does_not_pass_a_retargeted_pull_request()
    {
        using var store = GetDocumentStore();

        await HandleAsync(store, "release/dotnet-11.x", headRef: "backport/12-dotnet-10.x", recordedOnto: "release/dotnet-10.x");

        forge.Checks.Should().ContainSingle().Which.Conclusion.Should().Be("failure");
    }

    [Fact]
    public async Task A_persons_pull_request_into_a_past_line_passes()
    {
        using var store = GetDocumentStore();

        await HandleAsync(store, "release/dotnet-10.x");

        var check = forge.Checks.Should().ContainSingle().Which;
        check.Conclusion.Should().Be("success");
        check.Title.Should().Be("dotnet-10.x is a past line");
    }

    [Fact]
    public async Task Each_ecosystem_has_its_own_current_line()
    {
        using var store = GetDocumentStore();

        await HandleAsync(store, "release/angular-22.x");

        forge.Checks.Should().ContainSingle().Which.Conclusion.Should().Be("failure");
    }

    [Fact]
    public async Task A_base_that_is_no_line_or_a_repository_without_lines_gets_no_check()
    {
        using var store = GetDocumentStore();
        await HandleAsync(store, "feature/x");

        Yml("versioning:\n  mode: server\n");
        await HandleAsync(store, "release/dotnet-11.x");

        forge.Checks.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_forge_failure_publishes_nothing_and_does_not_throw(bool denied)
    {
        using var store = GetDocumentStore();
        git.ListBranchesAsync(Arg.Any<Repository>(), "release/", Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<string>>(_ => throw (denied
                ? new ForgeAccessDeniedException("Resource not accessible by integration", "contents=read")
                : new ForgeRequestException("GitHub answered 502.", 502)));

        await HandleAsync(store, "release/dotnet-11.x");

        forge.Checks.Should().BeEmpty();
    }

    [Fact]
    public async Task A_forge_without_the_versioning_seam_gets_no_check()
    {
        using var store = GetDocumentStore();
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new Repository { FullName = "acme/widgets", Provider = EForgeProvider.GitHub, DefaultBranch = "main" }, RepositoryId);
            await seed.SaveChangesAsync();
        }

        using var session = store.OpenAsyncSession();
        await new LinePolicyRecipient(session, new SingleForgeResolver(integration), [], [git], NullLogger<LinePolicyRecipient>.Instance)
            .HandleAsync(new LinePolicyMessage { RepositoryId = RepositoryId, Number = 7, HeadSha = Head, HeadRef = "fix/thing", BaseRef = "release/dotnet-11.x" });

        await integration.DidNotReceiveWithAnyArgs().GetFileContentAsync(default!, default!, default!, default);
    }

    [Theory]
    [InlineData("line", true)]
    [InlineData("bot", true)]
    [InlineData("default-branch", false)]
    [InlineData("archived", false)]
    [InlineData("unknown-repository", false)]
    public async Task Every_pull_request_not_into_the_default_branch_is_forwarded(string kind, bool forwarded)
    {
        using var store = GetDocumentStore();
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new Repository { FullName = "acme/widgets", Provider = EForgeProvider.GitHub, DefaultBranch = "main", Archived = kind == "archived" }, RepositoryId);
            await seed.SaveChangesAsync();
        }
        var bus = new RecordingMessageBus();
        var evt = new PullRequestUpdated(kind == "unknown-repository" ? "Repositories/404" : RepositoryId, 7, Head, "backport/6-dotnet-11.x",
            kind == "default-branch" ? "main" : "release/dotnet-11.x", new string('b', 40), "fix: guard", IsFirstOpen: false, AuthorIsBot: kind == "bot");

        using var session = store.OpenAsyncSession();
        await new LinePolicyEventsRecipient(session, bus).HandleAsync(new ForgeWebhookMessage<PullRequestUpdated>(EForgeProvider.GitHub, evt));

        if (forwarded)
            bus.Of<LinePolicyMessage>().Should().ContainSingle().Which.Should().BeEquivalentTo(new LinePolicyMessage
            {
                RepositoryId = RepositoryId, Number = 7, HeadSha = Head, HeadRef = "backport/6-dotnet-11.x", BaseRef = "release/dotnet-11.x",
            });
        else
            bus.Messages.Should().BeEmpty();
    }
}
