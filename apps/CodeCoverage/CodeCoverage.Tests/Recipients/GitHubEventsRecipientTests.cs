using CodeCoverage.Entities;
using CodeCoverage.Recipients;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Webhooks.GitHub.Messages;
using NSubstitute;
using Octokit.Webhooks;
using Raven.Client.Documents.Session;
using CodeCoverage.Tests;
using Raven.TestDriver;
using Xunit;

namespace CodeCoverage.Tests.Recipients;

/// <summary>
/// The webhook handler had no tests at all, and the defect that prompted these
/// is invisible to inspection of either writer on its own: push and
/// pull_request both wrote <c>Commit.ParentSha</c>, meaning different things,
/// and webhook delivery is unordered — so what the field meant depended on
/// which event GitHub happened to deliver last.
///
/// Events are fed as raw JSON, which is the real seam: the recipient
/// deserializes <c>EventJson</c> itself, so this exercises the same path
/// production does, including Octokit's converters.
/// </summary>
public class GitHubEventsRecipientTests : CoverageRavenTest
{
    private const long RepoId = 555;
    private const string HeadSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string BaseSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string PreviousTip = "cccccccccccccccccccccccccccccccccccccccc";

    /// <summary>Captures broadcasts so tests can assert what the webhook enqueued.</summary>
    private sealed class RecordingMessageBus : MintPlayer.Spark.Messaging.Abstractions.IMessageBus
    {
        public List<object> Messages { get; } = [];

        public Task BroadcastAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
        {
            Messages.Add(message!);
            return Task.CompletedTask;
        }

        public Task BroadcastOnceAsync<TMessage>(TMessage message, string deduplicationKey, CancellationToken cancellationToken = default)
        {
            Messages.Add(message!);
            return Task.CompletedTask;
        }

        public Task DelayBroadcastAsync<TMessage>(TMessage message, TimeSpan delay, CancellationToken cancellationToken = default)
        {
            Messages.Add(message!);
            return Task.CompletedTask;
        }
    }

    private static GitHubEventsRecipient CreateRecipient(IAsyncDocumentSession session)
        => CreateRecipient(session, out _);

    /// <summary>
    /// Overload that hands back the bus, so a test can assert what the webhook
    /// enqueued rather than only what it persisted. The absence of this was a
    /// real gap: the publish-on-open broadcast shipped with no test proving the
    /// webhook emits it at all.
    /// </summary>
    private static GitHubEventsRecipient CreateRecipient(IAsyncDocumentSession session, out RecordingMessageBus bus)
        => CreateRecipient(session, out bus, out _);

    /// <summary>
    /// Overload that also hands back the installation service, so a test can assert which branches
    /// were deleted and make the delete fail the way GitHub does.
    /// </summary>
    /// <remarks>
    /// The recording service existed before any test read it — it was added only so the class would
    /// construct once the recipient started injecting it. Handing it back is what turns it from a
    /// stub into coverage.
    /// </remarks>
    private static GitHubEventsRecipient CreateRecipient(
        IAsyncDocumentSession session, out RecordingMessageBus bus, out RecordingInstallationService installer)
    {
        bus = new RecordingMessageBus();
        installer = new RecordingInstallationService();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddSingleton(session);
        services.AddSingleton<MintPlayer.Spark.Messaging.Abstractions.IMessageBus>(bus);
        // Required since branch deletion landed: the recipient [Inject]s it, so without a
        // registration every test in this class fails at construction — which is how the feature
        // originally shipped, having no tests of its own.
        services.AddSingleton<MintPlayer.Spark.Webhooks.GitHub.Services.IGitHubInstallationService>(installer);
        services.AddScoped<GitHubEventsRecipient>();
        return services.BuildServiceProvider().GetRequiredService<GitHubEventsRecipient>();
    }

    /// <summary>
    /// Records the ref-delete calls the recipient makes, and can be told to fail like GitHub does.
    /// </summary>
    private sealed class RecordingInstallationService : MintPlayer.Spark.Webhooks.GitHub.Services.IGitHubInstallationService
    {
        public List<string> Deleted { get; } = [];
        public Exception? DeleteThrows { get; set; }

        public Task<Octokit.IGitHubClient> CreateInstallationClientAsync(long installationId)
        {
            var reference = Substitute.For<Octokit.IReferencesClient>();
            reference
                .When(r => r.Delete(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()))
                .Do(call =>
                {
                    if (DeleteThrows is not null) throw DeleteThrows;
                    Deleted.Add($"{call.ArgAt<string>(0)}/{call.ArgAt<string>(1)}:{call.ArgAt<string>(2)}");
                });

            var git = Substitute.For<Octokit.IGitDatabaseClient>();
            git.Reference.Returns(reference);

            var client = Substitute.For<Octokit.IGitHubClient>();
            client.Git.Returns(git);
            return Task.FromResult(client);
        }

        public Task<Octokit.IGitHubClient> CreateAppClientAsync() => throw new NotSupportedException();

        public Task<Octokit.GraphQL.Connection> CreateGraphQLConnectionAsync(
            long installationId, MintPlayer.Spark.Webhooks.GitHub.Services.EClientType clientType)
            => throw new NotSupportedException();
    }

    private static GitHubWebhookMessage Message(string eventType, string json) => new()
    {
        Headers = new WebhookHeaders(),
        InstallationId = 1,
        RepositoryFullName = "acme/widgets",
        EventType = eventType,
        EventJson = json,
    };

    // Octokit's models declare most of the payload required, so these mirror a
    // real delivery rather than the handful of fields the handler reads.
    private const string UserJson = """
        {
          "login": "acme", "id": 99, "node_id": "U_1", "type": "Organization",
          "avatar_url": "https://avatars.example/u/99",
          "gravatar_id": "", "url": "https://api.github.com/users/acme",
          "html_url": "https://github.com/acme",
          "followers_url": "https://api.github.com/users/acme/followers",
          "following_url": "https://api.github.com/users/acme/following{/other_user}",
          "gists_url": "https://api.github.com/users/acme/gists{/gist_id}",
          "starred_url": "https://api.github.com/users/acme/starred{/owner}{/repo}",
          "subscriptions_url": "https://api.github.com/users/acme/subscriptions",
          "organizations_url": "https://api.github.com/users/acme/orgs",
          "repos_url": "https://api.github.com/users/acme/repos",
          "events_url": "https://api.github.com/users/acme/events{/privacy}",
          "received_events_url": "https://api.github.com/users/acme/received_events",
          "site_admin": false
        }
        """;

    private static readonly string RepositoryJson = $$"""
        {
          "id": {{RepoId}}, "node_id": "R_1", "name": "widgets", "full_name": "acme/widgets",
          "private": false, "owner": {{UserJson}},
          "html_url": "https://github.com/acme/widgets",
          "description": null, "fork": false,
          "url": "https://api.github.com/repos/acme/widgets",
          "created_at": "2026-01-01T00:00:00Z",
          "updated_at": "2026-01-01T00:00:00Z",
          "pushed_at": "2026-01-01T00:00:00Z",
          "git_url": "git://github.com/acme/widgets.git",
          "ssh_url": "git@github.com:acme/widgets.git",
          "clone_url": "https://github.com/acme/widgets.git",
          "svn_url": "https://github.com/acme/widgets",
          "homepage": null, "size": 1, "stargazers_count": 0, "watchers_count": 0,
          "language": null, "has_issues": true, "has_projects": true, "has_downloads": true,
          "has_wiki": true, "has_pages": false, "forks_count": 0, "mirror_url": null,
          "archived": false, "disabled": false, "open_issues_count": 0, "license": null,
          "allow_forking": true, "is_template": false, "topics": [], "visibility": "public",
          "forks": 0, "open_issues": 0, "watchers": 0, "default_branch": "master"
        }
        """;

    private static string PushJson(string after, string before) => $$"""
        {
          "ref": "refs/heads/master",
          "before": "{{before}}",
          "after": "{{after}}",
          "created": false, "deleted": false, "forced": false,
          "base_ref": null,
          "compare": "https://github.com/acme/widgets/compare/x...y",
          "commits": [],
          "repository": {{RepositoryJson}},
          "pusher": { "name": "acme", "email": "acme@example.com" },
          "sender": {{UserJson}},
          "head_commit": {
            "id": "{{after}}", "tree_id": "t1", "distinct": true,
            "message": "a commit", "timestamp": "2026-08-18T09:00:00Z",
            "url": "https://github.com/acme/widgets/commit/{{after}}",
            "author": { "name": "Ada", "email": "ada@example.com" },
            "committer": { "name": "Ada", "email": "ada@example.com" },
            "added": [], "removed": [], "modified": []
          }
        }
        """;

    /// <summary>
    /// A fork's repository node — same shape, different <c>id</c>, which is the only thing
    /// <c>DeleteHeadBranchIfEnabled</c> compares (<c>headRepo.Id != baseRepo.Id</c>).
    /// </summary>
    private const long ForkRepoId = 556;

    private static readonly string ForkRepositoryJson =
        RepositoryJson.Replace($"\"id\": {RepoId},", $"\"id\": {ForkRepoId},")
                      .Replace("\"full_name\": \"acme/widgets\"", "\"full_name\": \"contributor/widgets\"");

    /// <param name="merged">
    /// Drives <c>merged</c>/<c>merged_at</c>/<c>state</c> together. They have to move as one: the
    /// branch-deletion gate tests <c>action == "closed" &amp;&amp; PullRequest.Merged == true</c>, and a
    /// payload claiming <c>merged: true</c> while still <c>state: "open"</c> is not a shape GitHub
    /// ever sends.
    /// </param>
    /// <param name="headFromFork">
    /// Puts the head branch in a different repository, so the fork arm can be exercised.
    /// </param>
    /// <remarks>
    /// ⚠️ The <c>installation</c> node is not decoration. <c>DeleteHeadBranchIfEnabled</c> returns
    /// early when <c>evt.Installation?.Id</c> is null, so without it every branch-deletion test
    /// would pass while asserting on an empty list — proving nothing. That arm is real and is
    /// covered by its own fact below.
    /// </remarks>
    private static string PullRequestJson(
        string headSha, string baseSha, string action = "opened",
        bool merged = false, bool headFromFork = false) => $$"""
        {
          "action": "{{action}}",
          {{(action == "synchronize" ? $"\"before\": \"{HeadSha}\", \"after\": \"{headSha}\"," : "")}}
          "number": 42,
          "installation": { "id": 1, "node_id": "MDIzOkludGVncmF0aW9uSW5zdGFsbGF0aW9uMQ==" },
          "repository": {{RepositoryJson}},
          "sender": {{UserJson}},
          "pull_request": {
            "url": "https://api.github.com/repos/acme/widgets/pulls/42",
            "id": 1, "node_id": "PR_1", "number": 42,
            "html_url": "https://github.com/acme/widgets/pull/42",
            "diff_url": "https://github.com/acme/widgets/pull/42.diff",
            "patch_url": "https://github.com/acme/widgets/pull/42.patch",
            "issue_url": "https://api.github.com/repos/acme/widgets/issues/42",
            "commits_url": "https://api.github.com/repos/acme/widgets/pulls/42/commits",
            "review_comments_url": "https://api.github.com/repos/acme/widgets/pulls/42/comments",
            "review_comment_url": "https://api.github.com/repos/acme/widgets/pulls/comments{/number}",
            "comments_url": "https://api.github.com/repos/acme/widgets/issues/42/comments",
            "statuses_url": "https://api.github.com/repos/acme/widgets/statuses/{{headSha}}",
            "state": "{{(merged ? "closed" : "open")}}", "locked": false, "title": "Add a thing",
            "user": {{UserJson}}, "body": null,
            "created_at": "2026-08-18T09:00:00Z", "updated_at": "2026-08-18T09:00:00Z",
            "closed_at": {{(merged ? "\"2026-08-18T10:00:00Z\"" : "null")}},
            "merged_at": {{(merged ? "\"2026-08-18T10:00:00Z\"" : "null")}},
            "merge_commit_sha": {{(merged ? $"\"{BaseSha}\"" : "null")}},
            "assignee": null, "assignees": [], "requested_reviewers": [],
            "requested_teams": [], "labels": [], "milestone": null,
            "draft": false, "author_association": "MEMBER", "active_lock_reason": null,
            "merged": {{(merged ? "true" : "false")}}, "mergeable": true, "rebaseable": true, "mergeable_state": "clean",
            "merged_by": null, "comments": 0, "review_comments": 0, "maintainer_can_modify": true,
            "commits": 1, "additions": 1, "deletions": 0, "changed_files": 1,
            "head": {
              "label": "acme:feature/thing", "ref": "feature/thing", "sha": "{{headSha}}",
              "user": {{UserJson}}, "repo": {{(headFromFork ? ForkRepositoryJson : RepositoryJson)}}
            },
            "base": {
              "label": "acme:master", "ref": "master", "sha": "{{baseSha}}",
              "user": {{UserJson}}, "repo": {{RepositoryJson}}
            },
            "_links": {
              "self": { "href": "https://api.github.com/repos/acme/widgets/pulls/42" },
              "html": { "href": "https://github.com/acme/widgets/pull/42" },
              "issue": { "href": "https://api.github.com/repos/acme/widgets/issues/42" },
              "comments": { "href": "https://api.github.com/repos/acme/widgets/issues/42/comments" },
              "review_comments": { "href": "https://api.github.com/repos/acme/widgets/pulls/42/comments" },
              "review_comment": { "href": "https://api.github.com/repos/acme/widgets/pulls/comments{/number}" },
              "commits": { "href": "https://api.github.com/repos/acme/widgets/pulls/42/commits" },
              "statuses": { "href": "https://api.github.com/repos/acme/widgets/statuses/{{headSha}}" }
            }
          }
        }
        """;

    private static async Task<Commit?> LoadCommit(IAsyncDocumentSession session, string sha)
        => await session.LoadAsync<Commit>(Commit.DocumentId(RepoId, sha));

    /// <summary>
    /// The publish-on-open trigger. Went to production untested on this side —
    /// the recipient that consumes the message was covered, but nothing proved
    /// the webhook emits it, and in production no comment appeared on either
    /// `opened` or `reopened`.
    /// </summary>
    [Theory]
    [InlineData("opened")]
    [InlineData("reopened")]
    public async Task Opening_or_reopening_a_pull_request_enqueues_the_pending_comment(string action)
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();

        await CreateRecipient(session, out var bus)
            .HandleAsync(Message("pull_request", PullRequestJson(HeadSha, BaseSha, action)));

        var opens = bus.Messages.OfType<CodeCoverage.Feedback.OpenPullRequestCommentMessage>().ToList();
        opens.Should().ContainSingle();
        opens[0].PullRequestNumber.Should().Be(42);
        opens[0].HeadSha.Should().Be(HeadSha);
        opens[0].AuthorIsBot.Should().BeFalse();
    }

    /// <summary>
    /// `synchronize` is served by the finalize path, which edits the same
    /// comment with real numbers — a pending comment there would replace a good
    /// number with "waiting".
    /// </summary>
    [Fact]
    public async Task Synchronize_does_not_enqueue_a_pending_comment()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();

        await CreateRecipient(session, out var bus)
            .HandleAsync(Message("pull_request", PullRequestJson(HeadSha, BaseSha, "synchronize")));

        bus.Messages.OfType<CodeCoverage.Feedback.OpenPullRequestCommentMessage>().Should().BeEmpty();
    }

    [Fact]
    public async Task A_pull_request_event_records_the_base_sha()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();

        await CreateRecipient(session).HandleAsync(Message("pull_request", PullRequestJson(HeadSha, BaseSha)));

        var commit = await LoadCommit(session, HeadSha);
        commit.Should().NotBeNull();
        commit!.ParentSha.Should().Be(BaseSha);
        commit.PullRequestNumber.Should().Be(42);
        commit.Branch.Should().Be("feature/thing");
    }

    /// <summary>
    /// The regression. A push landing after a PR event used to overwrite the PR
    /// base with the previous ref tip, silently and unrecoverably — the field
    /// stayed populated and plausible, which is what made it so hard to notice.
    /// </summary>
    [Fact]
    public async Task A_push_landing_after_a_pull_request_leaves_the_base_intact()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        var recipient = CreateRecipient(session);

        await recipient.HandleAsync(Message("pull_request", PullRequestJson(HeadSha, BaseSha)));
        await recipient.HandleAsync(Message("push", PushJson(after: HeadSha, before: PreviousTip)));

        var commit = await LoadCommit(session, HeadSha);
        commit!.ParentSha.Should().Be(BaseSha, "the pull_request webhook is the only writer of this field");
        commit.ParentSha.Should().NotBe(PreviousTip);
        // The push still contributes what it alone knows.
        commit.Message.Should().Be("a commit");
    }

    [Fact]
    public async Task A_push_alone_records_no_parent_sha()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();

        await CreateRecipient(session).HandleAsync(Message("push", PushJson(after: HeadSha, before: PreviousTip)));

        var commit = await LoadCommit(session, HeadSha);
        commit.Should().NotBeNull();
        commit!.Branch.Should().Be("master");
        commit.ParentSha.Should().BeNull("`before` is a ref tip, not this commit's parent");
    }

    /// <summary>
    /// GitHub re-sends `synchronize` with an updated base when the base branch
    /// advances, so the writer is `=` rather than `??=` — a frozen first-seen
    /// base would go quietly stale.
    /// </summary>
    [Fact]
    public async Task A_synchronize_updates_the_base_when_the_base_branch_moved()
    {
        const string movedBase = "dddddddddddddddddddddddddddddddddddddddd";
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        var recipient = CreateRecipient(session);

        await recipient.HandleAsync(Message("pull_request", PullRequestJson(HeadSha, BaseSha)));
        await recipient.HandleAsync(Message("pull_request", PullRequestJson(HeadSha, movedBase, action: "synchronize")));

        (await LoadCommit(session, HeadSha))!.ParentSha.Should().Be(movedBase);
    }

    [Fact]
    public async Task A_branch_creation_no_longer_stores_the_all_zero_sha()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();

        await CreateRecipient(session).HandleAsync(
            Message("push", PushJson(after: HeadSha, before: new string('0', 40))));

        (await LoadCommit(session, HeadSha))!.ParentSha.Should().BeNull();
    }

    // ---------------------------------------------------------------------------------------
    // #389 — branch deletion. An irreversible action against a user's repository that shipped
    // with no test of any kind. Each fact below is one arm of DeleteHeadBranchIfEnabled; the
    // arms are what keep it safe, so proving them individually is the point.
    // ---------------------------------------------------------------------------------------

    /// <summary>Seeds the base repository with the opt-in in whatever state the test needs.</summary>
    private static async Task SeedRepositoryAsync(IAsyncDocumentSession session, bool deleteBranchOnPrClose)
    {
        await session.StoreAsync(
            new Repository
            {
                GitHubId = RepoId,
                Name = "widgets",
                OwnerLogin = "acme",
                DeleteBranchOnPrClose = deleteBranchOnPrClose,
            },
            Repository.DocumentId(RepoId));
        await session.SaveChangesAsync();
    }

    private static string MergedPr(bool headFromFork = false)
        => PullRequestJson(HeadSha, BaseSha, action: "closed", merged: true, headFromFork: headFromFork);

    [Fact]
    public async Task A_merged_pull_request_deletes_its_head_branch_when_the_repository_opted_in()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        await SeedRepositoryAsync(session, deleteBranchOnPrClose: true);
        var recipient = CreateRecipient(session, out _, out var installer);

        await recipient.HandleAsync(Message("pull_request", MergedPr()));

        installer.Deleted.Should().Equal("acme/widgets:heads/feature/thing");
    }

    [Fact]
    public async Task A_closed_but_unmerged_pull_request_keeps_its_branch()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        await SeedRepositoryAsync(session, deleteBranchOnPrClose: true);
        var recipient = CreateRecipient(session, out _, out var installer);

        await recipient.HandleAsync(Message("pull_request",
            PullRequestJson(HeadSha, BaseSha, action: "closed", merged: false)));

        installer.Deleted.Should().BeEmpty("abandoning a pull request must not destroy the work on it");
    }

    [Fact]
    public async Task The_opt_in_is_respected()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        await SeedRepositoryAsync(session, deleteBranchOnPrClose: false);
        var recipient = CreateRecipient(session, out _, out var installer);

        await recipient.HandleAsync(Message("pull_request", MergedPr()));

        installer.Deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_repository_deletes_nothing()
    {
        // No Repository document at all — the load returns null and the method must return, not throw.
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        var recipient = CreateRecipient(session, out _, out var installer);

        await recipient.HandleAsync(Message("pull_request", MergedPr()));

        installer.Deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task A_head_branch_in_a_fork_is_never_deleted()
    {
        // The opt-in is on the base repository and cannot speak for someone else's fork.
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        await SeedRepositoryAsync(session, deleteBranchOnPrClose: true);
        var recipient = CreateRecipient(session, out _, out var installer);

        await recipient.HandleAsync(Message("pull_request", MergedPr(headFromFork: true)));

        installer.Deleted.Should().BeEmpty();
    }

    /// <summary>
    /// The seventh arm, which the issue's table omits — and the one that would have made every
    /// other fact here pass vacuously, since the shared payload carried no <c>installation</c>
    /// node until these tests were written.
    /// </summary>
    [Fact]
    public async Task A_payload_with_no_installation_deletes_nothing()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        await SeedRepositoryAsync(session, deleteBranchOnPrClose: true);
        var recipient = CreateRecipient(session, out _, out var installer);

        var withoutInstallation = MergedPr()
            .Replace("\"installation\": { \"id\": 1, \"node_id\": \"MDIzOkludGVncmF0aW9uSW5zdGFsbGF0aW9uMQ==\" },", "");

        await recipient.HandleAsync(Message("pull_request", withoutInstallation));

        installer.Deleted.Should().BeEmpty();
    }

    /// <summary>
    /// Deleting the branch is a courtesy after the merge has already landed. Failing the webhook
    /// delivery over it would cost the event and change nothing about the merge — so every failure
    /// shape must complete.
    /// </summary>
    [Theory]
    [MemberData(nameof(DeleteFailures))]
    public async Task A_failed_delete_never_breaks_webhook_processing(string shape, Exception failure)
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        await SeedRepositoryAsync(session, deleteBranchOnPrClose: true);
        var recipient = CreateRecipient(session, out _, out var installer);
        installer.DeleteThrows = failure;

        var act = async () => await recipient.HandleAsync(Message("pull_request", MergedPr()));

        await act.Should().NotThrowAsync($"a {shape} response must not cost the webhook delivery");
    }

    private static Octokit.IResponse ResponseWith(System.Net.HttpStatusCode status)
    {
        var response = Substitute.For<Octokit.IResponse>();
        response.StatusCode.Returns(status);
        response.Body.Returns("{}");
        response.Headers.Returns(new Dictionary<string, string>());
        return response;
    }

    public static TheoryData<string, Exception> DeleteFailures() => new()
    {
        // Lost a race with GitHub's own delete_branch_on_merge, or deleted by hand.
        { "404 already gone", new Octokit.NotFoundException(ResponseWith(System.Net.HttpStatusCode.NotFound)) },
        // Normally a protected branch. Retrying would fail identically every time.
        { "422 protected", new Octokit.ApiValidationException() },
        // A raised permission the installation has not accepted — Octokit's typed shape.
        { "403 forbidden", new Octokit.ForbiddenException(ResponseWith(System.Net.HttpStatusCode.Forbidden)) },
        // ...and its untyped shape. Octokit raises a bare ApiException for some 403s, which is why
        // PullRequestCommentPublisher catches on StatusCode rather than on the exception type.
        { "403 as a bare ApiException", new Octokit.ApiException(ResponseWith(System.Net.HttpStatusCode.Forbidden)) },
        { "anything else", new InvalidOperationException("boom") },
    };
}
