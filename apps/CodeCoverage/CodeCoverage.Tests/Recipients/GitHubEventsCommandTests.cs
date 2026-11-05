using CodeCoverage.Forge;
using CodeCoverage.Tests.MergeQueue;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Xunit;
using static CodeCoverage.Tests.MergeQueue.MergeQueueWebhookTests;

namespace CodeCoverage.Tests.Recipients;

/// <summary>
/// The GitHub normaliser's commands and producer events other than the merge queue's: backports asked by a label added after
/// the merge and by <c>/backport</c> comment (#153 D10; the merge-time labels are in <see cref="GitHubEventsRecipientTests"/>), the dependency updater's comment commands, a published
/// release and a Dependabot alert (dependency-updates PRD §7.6, R10), and the deliveries that raise nothing.
/// </summary>
public class GitHubEventsCommandTests : CoverageRavenTest
{
    private static string Repo => $$$"""{"id":{{{MergeQueueHarness.RepoGitHubId}}},"full_name":"acme/widget"}""";

    private static string LabeledAfterMerge(string label, string sender = "maintainer", string senderType = "User") => $$$"""
        {"action":"labeled","number":7,"pull_request":{"number":7,"merged":true},"label":{"name":"{{{label}}}"},
         "repository":{{{Repo}}},"sender":{"login":"{{{sender}}}","type":"{{{senderType}}}"}}
        """;

    private static string Comment(string body, long? id = 555) => $$$"""
        {"action":"created","issue":{"number":7,"pull_request":{"url":"https://api.github.com/repos/acme/widget/pulls/7"}},
         "comment":{{{(id is null ? "{" : $"{{\"id\":{id},")}}}"body":{{{System.Text.Json.JsonSerializer.Serialize(body)}}},"user":{"login":"maintainer"}},
         "repository":{{{Repo}}},"sender":{"login":"maintainer","type":"User"}}
        """;

    private static List<BackportRequested> Backports(RecordingMessageBus bus)
        => bus.Of<ForgeWebhookMessage<BackportRequested>>().Select(m => m.Event).ToList();

    [Fact]
    public async Task A_backport_label_added_after_the_merge_asks_now_for_a_person_only()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, busy: false);

        var person = await DeliverAsync(store, "pull_request", LabeledAfterMerge("backport 8.x"));
        var bot = await DeliverAsync(store, "pull_request", LabeledAfterMerge("backport 8.x", "renovate[bot]", "Bot"));
        var other = await DeliverAsync(store, "pull_request", LabeledAfterMerge("merge-queue"));

        Backports(person).Should().ContainSingle().Which
            .Should().BeEquivalentTo(new BackportRequested(RepositoryId, 7, ["8.x"], "maintainer", EBackportSource.Label));
        bot.Messages.Should().BeEmpty("a bot's label asks for nothing, and is no queue command either");
        Backports(other).Should().BeEmpty();
        other.Of<ForgeWebhookMessage<MergeQueueCommandIssued>>().Should().ContainSingle("a non-backport label is still the queue's");
    }

    [Fact]
    public async Task A_backport_comment_asks_for_its_lines()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, busy: false);

        var bus = await DeliverAsync(store, "issue_comment", Comment("/backport 8.x, 9.x\nthanks"));

        Backports(bus).Should().ContainSingle().Which
            .Should().BeEquivalentTo(new BackportRequested(RepositoryId, 7, ["8.x", "9.x"], "maintainer", EBackportSource.Comment));
    }

    [Fact]
    public async Task A_dependency_command_is_raised_with_its_comment_and_argument()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, busy: false);

        var bus = await DeliverAsync(store, "issue_comment", Comment("hold `@angular/*`"));
        var noId = await DeliverAsync(store, "issue_comment", Comment("approve", id: null));

        bus.Of<ForgeWebhookMessage<DependencyCommandIssued>>().Should().ContainSingle().Which.Event
            .Should().Be(new DependencyCommandIssued(RepositoryId, 7, 555, EDependencyCommand.Hold, "@angular/*", "maintainer"));
        noId.Messages.Should().BeEmpty("a comment without an id cannot be answered");
    }

    [Fact]
    public async Task A_command_in_an_unknown_repository_is_dropped()
    {
        using var store = GetDocumentStore();

        var bus = await DeliverAsync(store, "issue_comment", Comment("/backport 8.x"));

        bus.Messages.Should().BeEmpty();
    }

    private static string Release(string action, string tag, string? publishedAt) => $$$"""
        {"action":"{{{action}}}","release":{"tag_name":"{{{tag}}}"{{{(publishedAt is null ? "" : $",\"published_at\":\"{publishedAt}\"")}}}},
         "repository":{{{Repo}}},"sender":{"login":"maintainer","type":"User"}}
        """;

    [Fact]
    public async Task A_published_release_is_raised_with_its_tag_and_time()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, busy: false);

        var published = await DeliverAsync(store, "release", Release("published", "v2.1.0", "2026-10-30T12:00:00Z"));
        var undated = await DeliverAsync(store, "release", Release("published", "v2.2.0", null));
        var created = await DeliverAsync(store, "release", Release("created", "v2.3.0", null));
        var untagged = await DeliverAsync(store, "release", Release("published", "", null));

        published.Of<ForgeWebhookMessage<ReleasePublished>>().Should().ContainSingle().Which.Event
            .Should().Be(new ReleasePublished(RepositoryId, "v2.1.0", new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero)));
        undated.Of<ForgeWebhookMessage<ReleasePublished>>().Should().ContainSingle().Which.Event.PublishedAt
            .Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        created.Messages.Should().BeEmpty();
        untagged.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task A_release_of_an_unknown_repository_is_dropped()
    {
        using var store = GetDocumentStore();

        var bus = await DeliverAsync(store, "release", Release("published", "v1.0.0", null));

        bus.Messages.Should().BeEmpty();
    }

    private static string Alert(string action, string ecosystem, string name = "lodash", bool advisory = true) => $$$"""
        {"action":"{{{action}}}","alert":{"dependency":{"package":{"ecosystem":"{{{ecosystem}}}","name":"{{{name}}}"}}
         {{{(advisory ? ",\"security_advisory\":{\"ghsa_id\":\"GHSA-xxxx-yyyy-zzzz\"}" : "")}}}},
         "repository":{{{Repo}}},"sender":{"login":"maintainer","type":"User"}}
        """;

    [Theory]
    [InlineData("created", "npm", "npm")]
    [InlineData("reopened", "NuGet", "nuget")]
    [InlineData("reintroduced", "pip", "pypi")]
    [InlineData("created", "maven", "maven")]
    [InlineData("created", "composer", "composer")]
    [InlineData("created", "rubygems", "rubygems")]
    [InlineData("created", "rust", "cargo")]
    [InlineData("created", "go", "go")]
    [InlineData("created", "actions", "github-actions")]
    public async Task A_dependabot_alert_is_raised_in_the_lookup_records_words(string action, string ecosystem, string expected)
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, busy: false);

        var bus = await DeliverAsync(store, "dependabot_alert", Alert(action, ecosystem));

        bus.Of<ForgeWebhookMessage<DependencyAlertRaised>>().Should().ContainSingle().Which.Event
            .Should().Be(new DependencyAlertRaised(RepositoryId, expected, "lodash", "GHSA-xxxx-yyyy-zzzz"));
    }

    [Fact]
    public async Task An_alert_raises_nothing_when_dismissed_unplanned_nameless_or_unknown()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, busy: false);

        var dismissed = await DeliverAsync(store, "dependabot_alert", Alert("dismissed", "npm"));
        var unplanned = await DeliverAsync(store, "dependabot_alert", Alert("created", "swift"));
        var nameless = await DeliverAsync(store, "dependabot_alert", Alert("created", "npm", name: ""));
        var noAdvisory = await DeliverAsync(store, "dependabot_alert", Alert("created", "npm", advisory: false));

        dismissed.Messages.Should().BeEmpty();
        unplanned.Messages.Should().BeEmpty();
        nameless.Messages.Should().BeEmpty();
        noAdvisory.Of<ForgeWebhookMessage<DependencyAlertRaised>>().Should().ContainSingle().Which.Event.Advisory.Should().BeNull();

        using var unknown = GetDocumentStore();
        (await DeliverAsync(unknown, "dependabot_alert", Alert("created", "npm"))).Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task An_event_type_the_normaliser_does_not_read_raises_nothing()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, busy: false);

        var bus = await DeliverAsync(store, "star", $$$"""{"action":"created","repository":{{{Repo}}}}""");

        bus.Messages.Should().BeEmpty();
    }

    [Theory]
    [InlineData("""{"action":"renamed"}""", 1)]
    [InlineData("""{"action":"renamed","organization":"acme"}""", 1)]
    [InlineData("""{"action":"renamed","organization":{"login":"acme"}}""", 1)]
    [InlineData("""{"action":"renamed","organization":{"id":"x","login":"acme"}}""", 1)]
    [InlineData("""{"action":"renamed","organization":{"id":77}}""", 1)]
    [InlineData("""{"action":"renamed","organization":{"id":77,"login":"acme-renamed"}}""", 2)]
    public async Task An_organization_rename_needs_the_accounts_id_and_login(string json, long documents)
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, busy: false);

        await DeliverAsync(store, "organization", json);

        // The seeded repository, plus the account only a complete rename creates (the last row is the control).
        (await store.Maintenance.SendAsync(new GetStatisticsOperation())).CountOfDocuments.Should().Be(documents);
    }
}
