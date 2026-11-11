using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.PackageVersioning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests.PackageVersioning;

/// <summary>
/// Release lines recovery plan, segment F (resilience PRD Q8, Q12): whom a held backport asks, the label, the held list's
/// row, the "Line blocked by #N" checks, the reminders, and what the release undoes.
/// </summary>
public class BackportAttentionTests : CoverageRavenTest
{
    private const string Line = "release/dotnet-11.x";
    private const int Held = 7;
    private const int Source = 107;
    private static readonly string Waiting = new('w', 40);

    private readonly Repository repository = new()
    {
        Id = "Repositories/621", FullName = "acme/widget", Provider = EForgeProvider.GitHub, GitHubId = 621, DefaultBranch = "master",
    };
    private readonly FakeForgeVersioning forge = new();
    private readonly IForgeMergeQueue queue = Substitute.For<IForgeMergeQueue>();
    private readonly IForgeLabels files = Substitute.For<IForgeLabels>();
    private readonly IForgeIntegration integration = Substitute.For<IForgeIntegration>();

    public BackportAttentionTests()
    {
        queue.Provider.Returns(EForgeProvider.GitHub);
        files.Provider.Returns(EForgeProvider.GitHub);
        integration.Provider.Returns(EForgeProvider.GitHub);
        // NSubstitute answers an unconfigured Task<string?> with "", which would read as an empty CODEOWNERS everywhere.
        integration.GetFileContentAsync(Arg.Any<Repository>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        files.ListPullRequestFilesAsync(Arg.Any<Repository>(), Held, Arg.Any<CancellationToken>()).Returns([new ForgePullRequestFile("src/Guard.cs", 1, 1)]);
    }

    private void SourceBy(string login, bool bot = false, bool fork = false)
        => forge.OtherPullRequests[Source] = new ForgePullRequestText(Source, "fix: guard", null, "fix", new string('h', 40), "master", false, true, bot, new string('m', 40), [])
        {
            AuthorLogin = login, HeadIsFork = fork,
        };

    private void CodeOwnersFile(string text, string location = ".github/CODEOWNERS")
        => integration.GetFileContentAsync(Arg.Any<Repository>(), Line, location, Arg.Any<CancellationToken>()).Returns(text);

    private static MergeQueueEntry Entry(int number = Held, params string[] paths) => new()
    {
        Number = number, BaseBranch = Line, HeadBranch = $"backport/{Source}-dotnet-11.x", EvictionMessage = "Checks failed: build.",
        EvictionPaths = [.. paths], ExpectedHeadSha = number == Held ? new string('e', 40) : Waiting,
        HeldSinceUtc = new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc),
    };

    private async Task SeedAsync(IDocumentStore store, string? outcome = null)
    {
        using var session = store.OpenAsyncSession();
        await session.StoreAsync(new BackportPullRequest { Repository = repository.Id!, Number = Held, Source = Source, SourceSha = new string('s', 40), Branch = Line },
            BackportPullRequest.DocumentId(repository.Id!, Held));
        if (outcome is not null)
            await session.StoreAsync(new BackportAttempt { Repository = repository.Id!, Source = Source, Branch = Line, Outcome = outcome, PullRequest = Held, Detail = "held" },
                BackportAttempt.DocumentId(repository.Provider, repository.GitHubId, Source, Line));
        await session.SaveChangesAsync();
    }

    private async Task<T> WithAsync<T>(IDocumentStore store, Func<BackportAttention, Task<T>> act, string? baseUrl = "https://coverage.example/")
    {
        using var session = store.OpenAsyncSession();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Coverage:BaseUrl"] = baseUrl }).Build();
        var result = await act(new BackportAttention(new SingleForgeResolver(integration), [forge], [queue], [files], session, configuration,
            TimeProvider.System, NullLogger<BackportAttention>.Instance));
        await session.SaveChangesAsync();
        return result;
    }

    private async Task<BackportAttempt?> AttemptAsync(IDocumentStore store)
    {
        using var session = store.OpenAsyncSession();
        return await session.LoadAsync<BackportAttempt>(BackportAttempt.DocumentId(repository.Provider, repository.GitHubId, Source, Line));
    }

    [Fact]
    public async Task A_held_backport_asks_its_author_labels_it_lists_it_and_blocks_the_ones_behind()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        SourceBy("author");
        var held = Entry();

        var text = await WithAsync(store, a => a.HeldAsync(repository, held, [Entry(8)]));

        text.Should().Be("@author, this backports your #107, so it is yours to fix.\n\n"
            + "[Held backports](https://coverage.example/query/held-backports) lists every backport that waits for you.");
        await queue.Received(1).AddLabelAsync(repository, Held, BackportAttention.Label, Arg.Any<CancellationToken>());
        await queue.Received(1).AddAssigneesAsync(repository, Held, Arg.Is<IReadOnlyCollection<string>>(l => l.SequenceEqual(new[] { "author" })), Arg.Any<CancellationToken>());
        var check = forge.Checks.Should().ContainSingle().Which;
        check.Name.Should().Be("Line blocked by #7");
        check.Conclusion.Should().Be("neutral");
        held.BlockedHeads.Should().Equal([Waiting]);

        var attempt = await AttemptAsync(store);
        attempt!.Outcome.Should().Be(BackportAttempt.Held);
        attempt.SourceAuthor.Should().Be("author");
        attempt.PullRequest.Should().Be(Held);
        attempt.Detail.Should().Be("Checks failed: build.");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task A_forks_or_a_bots_backport_asks_the_code_owners_of_its_files(bool bot, bool fork)
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        SourceBy("someone", bot, fork);
        CodeOwnersFile("* @acme/core\nsrc/Guard.cs @guard @acme/security\n", "CODEOWNERS");

        var text = await WithAsync(store, a => a.HeldAsync(repository, Entry(), []), baseUrl: null);

        text.Should().Be("@guard, @acme/security: #107 came from a fork or a bot, so the code owners of its files are asked to fix it.");
        // People are assigned; a team is only mentioned.
        await queue.Received(1).AddAssigneesAsync(repository, Held, Arg.Is<IReadOnlyCollection<string>>(l => l.SequenceEqual(new[] { "guard" })), Arg.Any<CancellationToken>());
        (await AttemptAsync(store))!.SourceAuthor.Should().BeNull();
    }

    [Fact]
    public async Task The_paths_a_conflict_names_choose_the_owners()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        SourceBy("dependabot", bot: true);
        CodeOwnersFile("docs/ @docs-lead\nsrc/ @guard\n");

        var text = await WithAsync(store, a => a.HeldAsync(repository, Entry(Held, "docs/guide.md"), []), baseUrl: null);

        text.Should().StartWith("@docs-lead:");
        await files.DidNotReceiveWithAnyArgs().ListPullRequestFilesAsync(default!, default, default);
    }

    [Fact]
    public async Task Nobody_is_named_when_there_is_no_author_and_no_code_owner()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        SourceBy("dependabot", bot: true);

        var text = await WithAsync(store, a => a.HeldAsync(repository, Entry(), []), baseUrl: null);

        text.Should().StartWith("Nobody is named to fix it");
        await queue.DidNotReceiveWithAnyArgs().AddAssigneesAsync(default!, default, default!, default);
    }

    [Fact]
    public async Task A_forge_that_refuses_the_label_and_the_assignees_does_not_stop_the_hold()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        SourceBy("author");
        queue.AddLabelAsync(default!, default, default!, default).ThrowsAsyncForAnyArgs(new HttpRequestException("down"));
        queue.AddAssigneesAsync(default!, default, default!, default).ThrowsAsyncForAnyArgs(new NotSupportedException());

        var text = await WithAsync(store, a => a.HeldAsync(repository, Entry(), []));

        text.Should().StartWith("@author,");
        (await AttemptAsync(store))!.Outcome.Should().Be(BackportAttempt.Held);
    }

    [Fact]
    public async Task A_forge_that_cannot_say_who_opened_it_falls_back_to_the_code_owners()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        forge.ReadFailure = new HttpRequestException("down");
        CodeOwnersFile("* @lead\n");

        var text = await WithAsync(store, a => a.HeldAsync(repository, Entry(), []), baseUrl: null);

        text.Should().StartWith("@lead: #107 came from a fork or a bot");
    }

    [Fact]
    public async Task A_forge_that_cannot_read_the_code_owners_names_nobody()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        SourceBy("dependabot", bot: true);
        integration.GetFileContentAsync(Arg.Any<Repository>(), Line, ".github/CODEOWNERS", Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("down"));

        var text = await WithAsync(store, a => a.HeldAsync(repository, Entry(), []), baseUrl: null);

        text.Should().StartWith("Nobody is named to fix it");
    }

    [Fact]
    public async Task A_pull_request_without_a_head_to_check_gets_no_blocked_check()
    {
        using var store = GetDocumentStore();
        var held = Entry();
        var behind = Entry(8);
        behind.ExpectedHeadSha = string.Empty;
        behind.PinnedHeadSha = string.Empty;

        await WithAsync(store, async a => { await a.BlockedAsync(repository, held, behind); return true; });

        forge.Checks.Should().BeEmpty();
        held.BlockedHeads.Should().BeEmpty();
    }

    [Fact]
    public async Task The_first_reminder_asks_the_author_again()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        SourceBy("author");

        var text = await WithAsync(store, a => a.ReminderAsync(repository, Entry(), 1));

        text.Should().Be("@author, a reminder: This backport has held `release/dotnet-11.x` since 2026-11-09, and the backports behind it still wait.");
    }

    [Fact]
    public async Task The_second_reminder_escalates_to_the_code_owners_but_not_the_author()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        SourceBy("guard");
        CodeOwnersFile("src/ @guard @lead\n");

        var text = await WithAsync(store, a => a.ReminderAsync(repository, Entry(), 2));

        text.Should().StartWith("@lead: This backport has held").And.Contain("push a fix to `backport/107-dotnet-11.x`");
        await queue.Received(1).AddAssigneesAsync(repository, Held, Arg.Is<IReadOnlyCollection<string>>(l => l.SequenceEqual(new[] { "lead" })), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task There_is_no_reminder_when_nobody_is_left_to_ask()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        SourceBy("dependabot", bot: true);

        (await WithAsync(store, a => a.ReminderAsync(repository, Entry(), 1))).Should().BeNull();
        (await WithAsync(store, a => a.ReminderAsync(repository, Entry(), 2))).Should().BeNull();
    }

    [Fact]
    public async Task The_first_reminder_of_a_bots_backport_asks_its_code_owners()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        SourceBy("dependabot", bot: true);
        CodeOwnersFile("* @lead\n");

        var text = await WithAsync(store, a => a.ReminderAsync(repository, Entry(), 1));

        text.Should().StartWith("@lead, a reminder:");
    }

    [Fact]
    public async Task The_release_removes_the_label_reopens_the_row_and_passes_the_blocked_checks()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, BackportAttempt.Held);
        var entry = Entry();
        entry.BlockedHeads.Add(Waiting);

        await WithAsync(store, async a => { await a.ReleasedAsync(repository, entry); return true; });

        await queue.Received(1).RemoveLabelAsync(repository, Held, BackportAttention.Label, Arg.Any<CancellationToken>());
        var attempt = await AttemptAsync(store);
        attempt!.Outcome.Should().Be(BackportAttempt.Opened);
        attempt.Detail.Should().BeNull();
        var check = forge.Checks.Should().ContainSingle().Which;
        check.Name.Should().Be("Line blocked by #7");
        check.Conclusion.Should().Be("success");
        entry.BlockedHeads.Should().BeEmpty();
    }

    [Fact]
    public async Task The_release_leaves_a_row_that_is_no_longer_held_alone()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, BackportAttempt.Failed);

        await WithAsync(store, async a => { await a.ReleasedAsync(repository, Entry()); return true; });

        (await AttemptAsync(store))!.Outcome.Should().Be(BackportAttempt.Failed);
    }
}
