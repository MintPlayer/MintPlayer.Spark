using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.LookupReferences;
using CodeCoverage.PrSize;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Assertions;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests.PrSize;

/// <summary>
/// The labeller end to end on a real database (docs/coverage_pr_size_labels_PRD.md D1, D6, D7, D9): the forward from the
/// webhook lane, and the apply on its own lane, against an in-memory forge.
/// </summary>
public class PrSizeLabelRecipientTests : CoverageRavenTest
{
    private const string RepositoryId = "Repositories/527";
    private const string AccountId = "Accounts/github/1";
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private static async Task SeedAsync(IDocumentStore store, bool accountOn, EPrSizeLabelPolicy policy = EPrSizeLabelPolicy.Inherit, EForgeProvider provider = EForgeProvider.GitHub)
    {
        using var session = store.OpenAsyncSession();
        var account = new Account { PrSizeLabels = accountOn }.ApplyPrSizeDefaults();
        await session.StoreAsync(account, AccountId);
        await session.StoreAsync(new Repository { Account = AccountId, FullName = "acme/widgets", Provider = provider, PrSizeLabels = policy }, RepositoryId);
        await session.SaveChangesAsync();
    }

    private static async Task<List<object?>> ForwardAsync(IDocumentStore store, int? additions = 40, int? deletions = 2)
    {
        using var session = store.OpenAsyncSession();
        var bus = new RecordingMessageBus();
        var recipient = new PrSizeLabelEventsRecipient(session, bus);
        await recipient.HandleAsync(new ForgeWebhookMessage<PullRequestUpdated>(EForgeProvider.GitHub,
            new PullRequestUpdated(RepositoryId, 7, Sha, "feature", "master", "b", null, false, false, additions, deletions, 3)));
        return bus.Broadcast;
    }

    private static async Task ApplyAsync(IDocumentStore store, FakeForgeLabels forge, int? additions = 40, int? deletions = 2, int? changedFiles = 3)
    {
        using var session = store.OpenAsyncSession();
        var recipient = new PrSizeLabelRecipient(session, [forge], PrSizeLabelPacing.None, NullLogger<PrSizeLabelRecipient>.Instance);
        await recipient.HandleAsync(new ApplyPrSizeLabelMessage
        {
            RepositoryId = RepositoryId,
            Number = 7,
            HeadSha = Sha,
            Additions = additions,
            Deletions = deletions,
            ChangedFiles = changedFiles,
        });
    }

    private static async Task<Repository> LoadRepositoryAsync(IDocumentStore store)
    {
        using var session = store.OpenAsyncSession();
        return (await session.LoadAsync<Repository>(RepositoryId))!;
    }

    [Theory]
    [InlineData(false, EPrSizeLabelPolicy.Inherit, false)]
    [InlineData(true, EPrSizeLabelPolicy.Inherit, true)]
    [InlineData(false, EPrSizeLabelPolicy.Enabled, true)]
    [InlineData(true, EPrSizeLabelPolicy.Enabled, true)]
    [InlineData(true, EPrSizeLabelPolicy.Disabled, false)]
    [InlineData(false, EPrSizeLabelPolicy.Disabled, false)]
    public async Task The_webhook_is_forwarded_only_when_the_resolved_setting_is_on(bool accountOn, EPrSizeLabelPolicy policy, bool forwarded)
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, accountOn, policy);

        var broadcast = await ForwardAsync(store);

        if (forwarded)
            broadcast.Should().ContainSingle().Which.Should().BeOfType<ApplyPrSizeLabelMessage>().Which.Additions.Should().Be(40);
        else
            broadcast.Should().BeEmpty();
    }

    [Fact]
    public async Task Off_makes_no_forge_calls()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, accountOn: false);
        var forge = new FakeForgeLabels();

        await ApplyAsync(store, forge);

        forge.Writes.Should().BeEmpty();
        forge.Checks.Should().BeEmpty();
    }

    [Fact]
    public async Task The_first_pull_request_creates_the_labels_applies_one_and_completes_the_check()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, accountOn: true);
        var forge = new FakeForgeLabels();
        forge.Files.AddRange([new ForgePullRequestFile("src/a.cs", 30, 2), new ForgePullRequestFile("package-lock.json", 10, 0)]);

        await ApplyAsync(store, forge, additions: 40, deletions: 2);

        forge.PullRequests[7].Should().Equal("size/M");
        forge.Labels.Should().HaveCount(6);
        var check = forge.Checks.Should().ContainSingle().Which.Check;
        check.Completed.Should().BeTrue();
        check.Title.Should().Be("size/M · 32 lines changed");
        check.Text.Should().Contain("`package-lock.json`");
        (await LoadRepositoryAsync(store)).PrSizeLabelState.Should().HaveCount(6);
    }

    [Fact]
    public async Task A_total_inside_the_smallest_row_is_labelled_without_listing_files()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, accountOn: true);
        var forge = new FakeForgeLabels();

        await ApplyAsync(store, forge, additions: 4, deletions: 1);

        forge.FileListings.Should().Be(0);
        forge.PullRequests[7].Should().Equal("size/XS");
    }

    [Fact]
    public async Task Above_the_forge_file_cap_the_totals_decide()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, accountOn: true);
        var forge = new FakeForgeLabels();

        await ApplyAsync(store, forge, additions: 4000, deletions: 0, changedFiles: 3001);

        forge.FileListings.Should().Be(0);
        forge.PullRequests[7].Should().Equal("size/XXL");
    }

    [Fact]
    public async Task An_already_correct_label_is_not_written_again()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, accountOn: true);
        var forge = new FakeForgeLabels();
        forge.Files.Add(new ForgePullRequestFile("src/a.cs", 40, 2));
        await ApplyAsync(store, forge);
        forge.Writes.Clear();

        await ApplyAsync(store, forge);

        forge.Writes.Should().BeEmpty();
        forge.Checks.Should().ContainSingle();
    }

    [Fact]
    public async Task A_new_size_swaps_the_label_and_leaves_the_users_own_labels()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, accountOn: true);
        var forge = new FakeForgeLabels();
        forge.Files.Add(new ForgePullRequestFile("src/a.cs", 12, 0));
        await ApplyAsync(store, forge, additions: 12, deletions: 0);
        forge.PullRequests[7].Add("bug");
        forge.Files.Clear();
        forge.Files.Add(new ForgePullRequestFile("src/a.cs", 40, 2));
        forge.Writes.Clear();

        await ApplyAsync(store, forge);

        forge.Writes.Should().Equal("add #7 size/M", "remove #7 size/S");
        forge.PullRequests[7].Should().BeEquivalentTo(["bug", "size/M"]);
    }

    [Fact]
    public async Task A_forge_without_labels_is_a_no_op()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, accountOn: true, provider: EForgeProvider.Bitbucket);
        var forge = new FakeForgeLabels();

        await ApplyAsync(store, forge);

        forge.Writes.Should().BeEmpty();
        forge.Checks.Should().BeEmpty();
    }

    [Fact]
    public async Task An_archived_target_label_is_not_applied_and_the_check_says_so()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, accountOn: true);
        var forge = new FakeForgeLabels();
        forge.Files.Add(new ForgePullRequestFile("src/a.cs", 40, 2));
        forge.Labels.Add(new ForgeLabel("size/M", "eebb00", "Denotes a PR that changes 30-99 lines", true));

        await ApplyAsync(store, forge);

        forge.Writes.Should().NotContain(w => w.StartsWith("add #7"));
        var check = forge.Checks.Should().ContainSingle().Which.Check;
        check.Title.Should().Be("Size label not applied");
        check.Summary.Should().Contain("archived");
    }

    [Fact]
    public async Task A_missing_permission_completes_the_check_neutral_with_the_reason()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, accountOn: true);
        var forge = new FakeForgeLabels();
        forge.FailOn["files"] = new ForgeAccessDeniedException("Forbidden.", "pull_requests=read");

        await ApplyAsync(store, forge);

        var check = forge.Checks.Should().ContainSingle().Which.Check;
        check.Completed.Should().BeTrue();
        check.Title.Should().Be("Size label not applied");
        check.Summary.Should().Contain("pull_requests=read");
    }

    [Fact]
    public async Task An_unexpected_failure_still_completes_the_check_and_is_retried()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, accountOn: true);
        var forge = new FakeForgeLabels();
        forge.FailOn["files"] = new InvalidOperationException("boom");

        var apply = () => ApplyAsync(store, forge);

        await apply.Should().ThrowAsync<InvalidOperationException>();
        forge.Checks.Should().ContainSingle().Which.Check.Completed.Should().BeTrue();
    }

    [Fact]
    public void The_breakdown_is_capped()
    {
        var files = Enumerable.Range(0, 60).Select(i => new PrSizeFileResult($"f{i}.cs", 1, 0, 1, PrSizeFileOutcome.Counted, null)).ToArray();

        var text = PrSizeLabelRecipient.Breakdown(new PrSizeResult(60, files, 0))!;

        text.Should().Contain("`f49.cs`");
        text.Should().NotContain("`f50.cs`");
        text.Should().Contain("…and 10 more.");
    }

    [Fact]
    public async Task Remove_deletes_the_labels_and_clears_the_state()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, accountOn: true);
        var forge = new FakeForgeLabels();
        forge.Files.Add(new ForgePullRequestFile("src/a.cs", 40, 2));
        await ApplyAsync(store, forge);

        using (var session = store.OpenAsyncSession())
            await new PrSizeLabelRecipient(session, [forge], PrSizeLabelPacing.None, NullLogger<PrSizeLabelRecipient>.Instance)
                .HandleAsync(new RemovePrSizeLabelsMessage { RepositoryId = RepositoryId });

        forge.Labels.Should().BeEmpty();
        forge.PullRequests[7].Should().BeEmpty();
        (await LoadRepositoryAsync(store)).PrSizeLabelState.Should().BeEmpty();
    }

    private sealed class RecordingMessageBus : IMessageBus
    {
        public List<object?> Broadcast { get; } = [];

        public Task BroadcastAsync<TMessage>(TMessage message, BroadcastOptions options, CancellationToken cancellationToken = default)
            => BroadcastAsync(message, cancellationToken);

        public Task BroadcastAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
        {
            Broadcast.Add(message);
            return Task.CompletedTask;
        }

        public Task BroadcastOnceAsync<TMessage>(TMessage message, string deduplicationKey, CancellationToken cancellationToken = default)
            => BroadcastAsync(message, cancellationToken);

        public Task DelayBroadcastAsync<TMessage>(TMessage message, TimeSpan delay, CancellationToken cancellationToken = default)
            => BroadcastAsync(message, cancellationToken);
    }
}
