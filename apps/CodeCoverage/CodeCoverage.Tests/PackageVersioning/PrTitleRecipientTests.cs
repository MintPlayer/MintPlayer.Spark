using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.LookupReferences;
using CodeCoverage.PackageVersioning;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Assertions;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests.PackageVersioning;

/// <summary>
/// Title correction end to end on a real database (docs/coverage_package_versioning_PRD.md D14): the forward from the
/// webhook lane by the resolved setting, and the rewrite with its "PR title" check against an in-memory forge.
/// </summary>
public class PrTitleRecipientTests : CoverageRavenTest
{
    private const string RepositoryId = "Repositories/153";
    private const string AccountId = "Accounts/github/1";
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private static async Task SeedAsync(IDocumentStore store, bool accountOn, EPrTitlePolicy policy = EPrTitlePolicy.Inherit)
    {
        using var session = store.OpenAsyncSession();
        await session.StoreAsync(new Account { PrTitleCorrection = accountOn }, AccountId);
        await session.StoreAsync(new Repository { Account = AccountId, FullName = "acme/widgets", Provider = EForgeProvider.GitHub, PrTitleCorrection = policy }, RepositoryId);
        await session.SaveChangesAsync();
    }

    private static FakeForgeVersioning Forge(string title, string? body = null, string headRef = "feature", bool open = true) => new()
    {
        PullRequest = new ForgePullRequestText(7, title, body, headRef, Sha, "master", open, false, false, null, []),
    };

    private static async Task CorrectAsync(IDocumentStore store, FakeForgeVersioning forge, string? button = null, PrSize.FakeForgeLabels? labels = null)
    {
        using var session = store.OpenAsyncSession();
        await new PrTitleRecipient(session, [forge], [labels ?? new PrSize.FakeForgeLabels()], NullLogger<PrTitleRecipient>.Instance)
            .HandleAsync(new CorrectPrTitleMessage { RepositoryId = RepositoryId, Number = 7, Button = button });
    }

    [Theory]
    [InlineData(false, EPrTitlePolicy.Inherit, false)]
    [InlineData(true, EPrTitlePolicy.Inherit, true)]
    [InlineData(false, EPrTitlePolicy.Enabled, true)]
    [InlineData(true, EPrTitlePolicy.Disabled, false)]
    public async Task The_webhook_is_forwarded_only_when_the_resolved_setting_is_on(bool accountOn, EPrTitlePolicy policy, bool forwarded)
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, accountOn, policy);

        using var session = store.OpenAsyncSession();
        var bus = new RecordingMessageBus();
        await new PrTitleEventsRecipient(session, bus)
            .HandleAsync(new ForgeWebhookMessage<PullRequestDescribed>(EForgeProvider.GitHub, new PullRequestDescribed(RepositoryId, 7)));

        if (forwarded)
            bus.Broadcast.Should().ContainSingle().Which.Should().BeOfType<CorrectPrTitleMessage>().Which.Number.Should().Be(7);
        else
            bus.Broadcast.Should().BeEmpty();
    }

    [Fact]
    public async Task The_title_is_rewritten_and_the_check_says_why()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, accountOn: true);
        var forge = Forge("Crash on empty input", "Closes #12");
        forge.Issues[12] = ["bug"];

        await CorrectAsync(store, forge);

        forge.TitleUpdates.Should().Equal("fix: Crash on empty input (#12)");
        var check = forge.Checks.Should().ContainSingle().Which;
        check.Name.Should().Be(PrTitleRecipient.CheckName);
        check.Completed.Should().BeTrue();
        check.Conclusion.Should().Be("neutral");
        check.Summary.Should().Contain("labelled `bug`").And.Contain("#12");
    }

    [Fact]
    public async Task The_app_s_backport_pull_request_keeps_its_title_and_asks_nothing()
    {
        // Changelog × release lines R3: the title copies the source's corrected one behind its line, and the changelog entry
        // is the source's (#693 D5). Rewriting it only lost the [line] prefix and the (#n) reference.
        using var store = GetDocumentStore();
        await SeedAsync(store, accountOn: true);
        const string title = "[dotnet-10.x] fix: Crash on empty input (#12)";
        var forge = Forge(title, BackportProvenance.WithMarker("Backport of #12 to `release/dotnet-10.x`.", "acme/widgets", 12), "backport/12-dotnet-10.x");

        await CorrectAsync(store, forge);

        forge.TitleUpdates.Should().BeEmpty();
        var check = forge.Checks.Should().ContainSingle().Which;
        (check.Completed, check.Conclusion).Should().Be((true, (string?)"neutral"));
        check.Title.Should().Be("Title left alone");
        check.Summary.Should().Contain("#12");
        check.Actions.Should().BeEmpty();
    }

    /// <summary>The app's own edit comes back as an <c>edited</c> event; the second run must write nothing.</summary>
    [Fact]
    public async Task A_correct_title_is_left_alone()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, accountOn: true);
        var forge = Forge("Add the lister", "- [x] Feature");

        await CorrectAsync(store, forge);
        await CorrectAsync(store, forge);

        forge.TitleUpdates.Should().Equal("feat: Add the lister");
        forge.Checks.Select(c => c.Title).Should().Equal("Title set to \"feat: Add the lister\"", "Title already correct");
        forge.Checks.Should().OnlyContain(c => c.Actions!.Count == 0, "a typed title needs no buttons, and stale ones go");
    }

    // #693 D12: no silent feat. Without an explicit signal the title stays untyped and the check asks.
    [Fact]
    public async Task Without_a_signal_the_check_offers_the_buttons_and_a_click_types_the_title()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, accountOn: true);
        var forge = Forge("Add the lister", headRef: "fix/issue-12-lister");

        await CorrectAsync(store, forge);

        forge.TitleUpdates.Should().BeEmpty("the branch name is never read and nothing defaults to feat");
        var asking = forge.Checks.Single();
        asking.Title.Should().Be("Type of change?");
        asking.Actions.Should().Equal(PrTitleCheck.Buttons);
        asking.ExternalId.Should().Be("7");

        await CorrectAsync(store, forge, PrTitleCheck.Fix.Identifier);

        forge.TitleUpdates.Should().Equal("fix: Add the lister");
        forge.Checks.Last().Summary.Should().Contain("the button chosen on this check");
    }

    [Fact]
    public async Task Not_user_facing_labels_the_pull_request_and_the_check_stops_asking()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, accountOn: true);
        var forge = Forge("Bump the build image");
        var labels = new PrSize.FakeForgeLabels();

        await CorrectAsync(store, forge, PrTitleCheck.NotUserFacing.Identifier, labels);

        labels.PullRequests[7].Should().Equal(PrTitleCheck.SkipLabel);
        forge.TitleUpdates.Should().BeEmpty();
        var check = forge.Checks.Single();
        (check.Title, check.Actions!.Count).Should().Be(("Not user-facing", 0));

        // Labelled already (by the button or by hand): a later edit does not ask again.
        forge.PullRequest = forge.PullRequest! with { Labels = [PrTitleCheck.SkipLabel] };
        await CorrectAsync(store, forge, labels: labels);
        forge.Checks.Last().Title.Should().Be("Not user-facing");
    }

    [Fact]
    public async Task A_closed_pull_request_is_not_touched()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, accountOn: true);
        var forge = Forge("Add the lister", open: false);

        await CorrectAsync(store, forge);

        forge.TitleUpdates.Should().BeEmpty();
        forge.Checks.Should().BeEmpty();
    }

    [Fact]
    public async Task A_missing_permission_still_completes_the_check()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, accountOn: true);
        var forge = Forge("Add the lister", "- [x] Feature");
        forge.FailTitleUpdateWith = new ForgeAccessDeniedException("Resource not accessible by integration", "pull_requests=write");

        await CorrectAsync(store, forge);

        var check = forge.Checks.Should().ContainSingle().Which;
        check.Completed.Should().BeTrue();
        check.Title.Should().Be("Title not corrected");
        check.Summary.Should().Contain("pull_requests=write");
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
