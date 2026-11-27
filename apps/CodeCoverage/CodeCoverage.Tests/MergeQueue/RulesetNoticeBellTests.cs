using CodeCoverage.Dependencies.Onboarding;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.MergeQueue;
using CodeCoverage.Tests.CommitGraph;
using CodeCoverage.Tests.Services;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Notifications;
using NSubstitute;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests.MergeQueue;

/// <summary>
/// #714 Dec5, Dec11: the bell delivers each open notice once to each manager of its repository, when they open the app,
/// and links to the account page whose repository list carries "Set up rulesets".
/// </summary>
public class RulesetNoticeBellTests : CoverageRavenTest
{
    private const string Viewer = "SparkUsers/1";
    private static readonly DateTimeOffset Start = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);
    private static readonly string Managed = Repository.DocumentId(EForgeProvider.GitHub, 4711);
    private static readonly string Foreign = Repository.DocumentId(EForgeProvider.GitHub, 4712);
    private readonly MutableTime time = new(Start);
    private readonly List<SparkNotification> sent = [];

    /// <summary>
    /// The viewer manages <see cref="Managed"/> only. Keyed by <c>Id.Value</c> like the real service; a loaded repository's id
    /// comes back lower-cased, so the comparison ignores case.
    /// </summary>
    private sealed class ScriptedOnboarding : IDependencyOnboardingService
    {
        public int Checks { get; private set; }

        public Task<DependencyOnboardingResponse> ListAsync(IReadOnlyDictionary<string, string?> query, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, string?>> CheckAsync(IReadOnlyList<Repository> repositories, CancellationToken cancellationToken = default)
        {
            Checks++;
            return Task.FromResult<IReadOnlyDictionary<string, string?>>(
                repositories.ToDictionary(r => r.Id.Value,
                    r => string.Equals(r.Id.Value, Managed, StringComparison.OrdinalIgnoreCase) ? DependencyOnboardingService.Owner : null));
        }
    }

    private readonly ScriptedOnboarding onboarding = new();

    private async Task<IDocumentStore> StoreAsync()
    {
        var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        await session.StoreAsync(new Repository
        {
            Provider = EForgeProvider.GitHub,
            OwnerLogin = "acme",
            Name = "widget",
            FullName = "acme/widget",
            DefaultBranch = "master",
        }, Managed);
        await session.StoreAsync(new Repository
        {
            Provider = EForgeProvider.GitHub,
            OwnerLogin = "other",
            Name = "gadget",
            FullName = "other/gadget",
            DefaultBranch = "main",
        }, Foreign);
        await session.SaveChangesAsync();
        return store;
    }

    private async Task RaiseAsync(IDocumentStore store, string repositoryId, Func<IRulesetNotices, Repository, Task> raise)
    {
        using var session = store.OpenAsyncSession();
        var repository = (await session.LoadAsync<Repository>(repositoryId))!;
        await raise(new RulesetNotices(session, time), repository);
        await session.SaveChangesAsync();
    }

    /// <summary>Delivers as <paramref name="userId"/>; <paramref name="whileNotifying"/> runs inside each notify call.</summary>
    private async Task<int> DeliverAsync(IDocumentStore store, string? userId = Viewer, Func<Task>? whileNotifying = null)
    {
        var notifier = Substitute.For<ISparkNotifier>();
        notifier.NotifyAsync(Arg.Any<SparkNotification>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            sent.Add(call.ArgAt<SparkNotification>(0));
            if (whileNotifying is not null) await whileNotifying();
            return new SparkNotifyReceipt([], 0, 0);
        });
        var manager = Substitute.For<IManager>();
        manager.GetTranslatedMessage(Arg.Any<string>(), Arg.Any<object[]>())
            .Returns(call => call.ArgAt<string>(0) + string.Concat(call.ArgAt<object[]>(1).Select(a => $"|{a}")));

        using var session = store.OpenAsyncSession();
        return await new RulesetNoticeBell(session, new FakeSparkCurrentUser(userId), onboarding, notifier, manager).DeliverAsync();
    }

    private static async Task<RulesetNotice> LoadAsync(IDocumentStore store, string repositoryId, ERulesetNoticeKind kind)
    {
        using var session = store.OpenAsyncSession();
        return (await session.LoadAsync<RulesetNotice>(RulesetNotice.DocumentId(repositoryId, kind)))!;
    }

    private static ForgeBranchRules RequiringNames(params string[] names)
        => new(true, names, 0, false, false, false, false,
            [new ForgeRuleset(2, "Reviews and checks", ["required_status_checks"], EForgeRulesetBypass.Never, [.. names.Select(name => new ForgeRequiredCheck(name))])],
            EForgeClassicProtection.None);

    [Fact]
    public async Task A_manager_hears_about_each_open_notice_once_with_a_link_to_the_account_page()
    {
        using var store = await StoreAsync();
        await RaiseAsync(store, Managed, (n, r) => n.RaiseMissingCheckAsync(r, "master", ["test"], ["build"], 7));
        await RaiseAsync(store, Managed, (n, r) => n.RaisePinMismatchAsync(r, "master", new Dictionary<string, long> { ["build"] = 15368 }, 8));
        await RaiseAsync(store, Managed, (n, r) => n.OfferSummaryJobAsync(r, "master", RequiringNames("build", "lint")));

        (await DeliverAsync(store)).Should().Be(3);

        sent.Should().HaveCount(3);
        sent.Should().OnlyContain(n => n.UserIds.Single() == Viewer && n.Url == "/github/a/acme" && n.Category == RulesetNoticeBell.Category);
        sent.Select(n => n.Title).Should().BeEquivalentTo([
            "rulesets.bell.missingCheck.title|acme/widget",
            "rulesets.bell.pinMismatch.title|acme/widget",
            "rulesets.bell.summaryJob.title|acme/widget",
        ]);
        sent.Single(n => n.Title.StartsWith("rulesets.bell.missingCheck", StringComparison.Ordinal)).Body
            .Should().Be("rulesets.bell.missingCheck.body|test|7 rulesets.bell.howTo|acme/widget");
        (await LoadAsync(store, Managed, ERulesetNoticeKind.MissingCheck)).NotifiedUsers.Should().Equal([Viewer]);

        sent.Clear();
        (await DeliverAsync(store)).Should().Be(0, "a manager is told once about one drift");
        sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_new_drift_on_the_same_notice_is_delivered_again()
    {
        using var store = await StoreAsync();
        await RaiseAsync(store, Managed, (n, r) => n.RaiseMissingCheckAsync(r, "master", ["test"], ["build"], 7));
        (await DeliverAsync(store)).Should().Be(1);

        await RaiseAsync(store, Managed, (n, r) => n.RaiseMissingCheckAsync(r, "master", ["tests"], ["build"], 9));

        (await DeliverAsync(store)).Should().Be(1);
        sent[^1].Body.Should().StartWith("rulesets.bell.missingCheck.body|tests|9");
    }

    /// <summary>
    /// The queue raises a new drift on the same document while the bell is delivering the old one: the viewer is not marked
    /// as told about the new one, so the next app open delivers it.
    /// </summary>
    [Fact]
    public async Task A_drift_raised_while_the_bell_delivers_still_reaches_the_viewer()
    {
        using var store = await StoreAsync();
        await RaiseAsync(store, Managed, (n, r) => n.RaiseMissingCheckAsync(r, "master", ["test"], ["build"], 7));

        (await DeliverAsync(store, whileNotifying: () => RaiseAsync(store, Managed,
            (n, r) => n.RaiseMissingCheckAsync(r, "master", ["tests"], ["build"], 9)))).Should().Be(1);

        (await LoadAsync(store, Managed, ERulesetNoticeKind.MissingCheck)).NotifiedUsers.Should().BeEmpty();
        (await DeliverAsync(store)).Should().Be(1);
        (await LoadAsync(store, Managed, ERulesetNoticeKind.MissingCheck)).NotifiedUsers.Should().Equal([Viewer]);
    }

    [Fact]
    public async Task Only_a_manager_is_told_and_a_resolved_notice_is_not_delivered()
    {
        using var store = await StoreAsync();
        await RaiseAsync(store, Foreign, (n, r) => n.RaiseMissingCheckAsync(r, "main", ["test"], ["build"], 3));
        await RaiseAsync(store, Managed, (n, r) => n.RaiseMissingCheckAsync(r, "master", ["test"], ["build"], 7));
        await RaiseAsync(store, Managed, (n, r) => n.ResolveAllAsync(r.Id.Value));

        (await DeliverAsync(store)).Should().Be(0);

        sent.Should().BeEmpty();
        (await LoadAsync(store, Foreign, ERulesetNoticeKind.MissingCheck)).NotifiedUsers.Should().BeEmpty("its managers are still to be told");
    }

    [Fact]
    public async Task An_anonymous_caller_or_nothing_open_costs_no_manager_check()
    {
        using var store = await StoreAsync();
        (await DeliverAsync(store)).Should().Be(0);

        await RaiseAsync(store, Managed, (n, r) => n.RaiseMissingCheckAsync(r, "master", ["test"], ["build"], 7));
        (await DeliverAsync(store, userId: null)).Should().Be(0);

        onboarding.Checks.Should().Be(0);
        sent.Should().BeEmpty();
    }
}
