using CodeCoverage.Actions;
using CodeCoverage.Dependencies.Lifecycle;
using CodeCoverage.Dependencies.Onboarding;
using CodeCoverage.Entities;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Queries;
using NSubstitute;
using Xunit;

namespace CodeCoverage.Tests.Actions;

/// <summary>
/// The onboarding grid's rows (dependency-updates PRD D-O14): the service's listing as Spark rows, only those with a Repository
/// document, the status columns as translated text, and the scope parent's id handed to the service as the setup query.
/// </summary>
public class DependencyOnboardingRowActionsTests
{
    private sealed class ScriptedOnboarding(params DependencyOnboardingEntry[] entries) : IDependencyOnboardingService
    {
        public List<IReadOnlyDictionary<string, string?>> Queries { get; } = [];

        public Task<DependencyOnboardingResponse> ListAsync(IReadOnlyDictionary<string, string?> query, CancellationToken cancellationToken = default)
        {
            Queries.Add(query);
            return Task.FromResult(new DependencyOnboardingResponse("octocat", "Ok", true, false, null, entries));
        }

        public Task<IReadOnlyDictionary<string, string?>> CheckAsync(IReadOnlyList<Repository> repositories, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private static DependencyOnboardingEntry Entry(string? id, string fullName, bool enabled = false, string onboarding = "None", bool syncing = false,
        DependencyPullRequestLink? pr = null, string? inFile = null, bool addsQueue = true, string managedBy = DependencyOnboardingService.Owner)
        => new(id, "github", fullName, "acme", false, managedBy, "Inherit", enabled, false, onboarding, null, pr, syncing, addsQueue, inFile);

    private static IManager Translating()
    {
        var manager = Substitute.For<IManager>();
        manager.GetTranslatedMessage(Arg.Any<string>(), Arg.Any<object[]>()).Returns(call => $"T({call.ArgAt<string>(0)})");
        return manager;
    }

    private static CustomQueryArgs Args(PersistentObject? parent = null) => new()
    {
        Query = new SparkQuery { Id = Guid.NewGuid(), Name = "DependencyOnboardingRows", Source = "Custom.DependencyOnboardingRows" },
        Parent = parent,
        ParentType = parent?.Name,
    };

    [Fact]
    public async Task Rows_are_the_listed_repositories_with_a_document_and_their_state_as_translated_text()
    {
        var onboarding = new ScriptedOnboarding(
            Entry("Repositories/github/1", "acme/off"),
            Entry("Repositories/github/2", "acme/opened", enabled: true, onboarding: "PullRequestOpened",
                pr: new DependencyPullRequestLink(7, "https://github.com/acme/opened/pull/7", false), inFile: "enabled", addsQueue: false,
                managedBy: DependencyOnboardingService.RepoAdmin),
            Entry("Repositories/github/3", "acme/branchless", syncing: true),
            Entry("Repositories/github/4", "acme/no-url", enabled: true, onboarding: "Blocked", pr: new DependencyPullRequestLink(9, null, false)),
            Entry(null, "acme/fresh", syncing: true));

        var rows = (await new DependencyOnboardingRowActions(onboarding, Translating()).DependencyOnboardingRows(Args())).ToList();

        rows.Select(r => r.Id).Should().Equal(["Repositories/github/1", "Repositories/github/2", "Repositories/github/3", "Repositories/github/4"],
            "a repository without a document has no id the action could take: the page's banner counts it instead");
        rows[0].State.Should().Be("T(dependencyOnboarding.state.off)");
        rows[0].ManagedBy.Should().Be("T(dependencyOnboarding.managedBy.owner)");
        rows[0].OnboardingPullRequest.Should().BeNull();
        rows[0].MergeQueueInCoverageYml.Should().Be("T(dependencyOnboardingUi.mergeQueueInFile.unknown)");
        rows[0].OnboardingMergeQueue.Should().Be("T(dependencyOnboarding.queueWillAdd)");
        rows[1].State.Should().Be("T(dependencyOnboarding.state.PullRequestOpened)");
        rows[1].ManagedBy.Should().Be("T(dependencyOnboarding.managedBy.repo-admin)");
        rows[1].OnboardingPullRequest.Should().Be("https://github.com/acme/opened/pull/7");
        rows[1].MergeQueueInCoverageYml.Should().Be("T(dependencyOnboardingUi.mergeQueueInFile.enabled)");
        rows[1].OnboardingMergeQueue.Should().Be("T(dependencyOnboarding.queueWontAdd)");
        rows[2].State.Should().Be("T(dependencyOnboarding.state.syncing)", "a document without a default branch is still syncing");
        rows[3].State.Should().Be("T(dependencyOnboarding.state.Blocked)");
        rows[3].OnboardingPullRequest.Should().Be("#9", "the forge gave no URL");
    }

    /// <summary>
    /// P1 (production re-test 2026-10-17): a repository onboarded in an earlier test and set back to "Use account setting"
    /// (account default off) showed as onboarded, because its run document still said <c>Done</c>.
    /// </summary>
    [Theory]
    [InlineData("Done")]
    [InlineData("PullRequestOpened")]
    [InlineData("Blocked")]
    public async Task A_repository_whose_setting_resolves_off_shows_off_whatever_its_earlier_onboarding_left(string onboarding)
    {
        var row = (await new DependencyOnboardingRowActions(new ScriptedOnboarding(Entry("Repositories/github/1", "acme/was-on", enabled: false, onboarding: onboarding)), Translating())
            .DependencyOnboardingRows(Args())).Single();

        row.State.Should().Be("T(dependencyOnboarding.state.off)");
    }

    [Fact]
    public async Task An_untranslated_state_falls_back_to_the_servers_own_word()
    {
        var manager = Substitute.For<IManager>();
        manager.GetTranslatedMessage(Arg.Any<string>(), Arg.Any<object[]>()).Returns(call => call.ArgAt<string>(0));

        var row = (await new DependencyOnboardingRowActions(new ScriptedOnboarding(Entry("Repositories/github/1", "acme/x", enabled: true, onboarding: "Done")), manager)
            .DependencyOnboardingRows(Args())).Single();

        row.State.Should().Be("Done");
        row.ManagedBy.Should().Be("owner");
    }

    /// <summary>
    /// D-O20 (live test 2026-10-17): "Run now" ended without a pull request and the page said nothing. Once onboarding
    /// has no note of its own, the Note is how the last run ended; an onboarding note still wins.
    /// </summary>
    [Fact]
    public async Task The_note_says_how_the_last_run_ended_once_onboarding_has_nothing_to_say()
    {
        var manager = Substitute.For<IManager>();
        manager.GetTranslatedMessage("dependencyOnboarding.lastRun", Arg.Any<object[]>())
            .Returns(call => $"Last run: {call.ArgAt<object[]>(1)[0]}: {call.ArgAt<object[]>(1)[1]}");
        var onboarding = new ScriptedOnboarding(
            Entry("Repositories/github/1", "acme/quiet", enabled: true, onboarding: "Done") with
            {
                LastRunOutcome = "nothing-planned", LastRunMessage = "Nothing within your thresholds to apply this time; 12 groups awaiting approval.",
            },
            Entry("Repositories/github/2", "acme/blocked", enabled: true, onboarding: "Blocked") with
            {
                OnboardingNote = "The App lacks contents: write.", LastRunOutcome = "permission", LastRunMessage = "refused",
            },
            Entry("Repositories/github/3", "acme/never", enabled: true, onboarding: "Done"));

        var rows = (await new DependencyOnboardingRowActions(onboarding, manager).DependencyOnboardingRows(Args())).ToList();

        rows[0].Note.Should().Be("Last run: nothing-planned: Nothing within your thresholds to apply this time; 12 groups awaiting approval.");
        rows[1].Note.Should().Be("The App lacks contents: write.");
        rows[2].Note.Should().BeNull("no run yet");
    }

    [Fact]
    public async Task The_scope_parents_id_is_the_setup_query_and_a_repeated_key_is_comma_joined()
    {
        var onboarding = new ScriptedOnboarding();
        var actions = new DependencyOnboardingRowActions(onboarding, Translating());

        await actions.DependencyOnboardingRows(Args(new PersistentObject
        {
            ObjectTypeId = Guid.Empty, Id = "installation_id=5555&installation_id=6666&setup_action=install", Name = DependencyOnboardingScope.TypeName,
        }));
        await actions.DependencyOnboardingRows(Args());
        await actions.DependencyOnboardingRows(Args(new PersistentObject { ObjectTypeId = Guid.Empty, Id = "Repositories/github/1", Name = "Repository" }));
        await actions.DependencyOnboardingRows(Args(new PersistentObject { ObjectTypeId = Guid.Empty, Id = "installation_id=", Name = DependencyOnboardingScope.TypeName }));

        string.Join(";", onboarding.Queries[0].OrderBy(q => q.Key, StringComparer.Ordinal).Select(q => $"{q.Key}={q.Value}")).Should().Be("installation_id=5555,6666;setup_action=install");
        onboarding.Queries[1].Count.Should().Be(0, "no scope lists everything the viewer manages");
        onboarding.Queries[2].Count.Should().Be(0, "only the scope type narrows; another parent is ignored");
        string.Join(";", onboarding.Queries[3].Select(q => $"{q.Key}={q.Value}")).Should().Be("installation_id=",
            "an empty installation_id is kept, so the forge reads it as an unknown installation (D-O13)");
    }
}
