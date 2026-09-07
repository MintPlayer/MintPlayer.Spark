using CodeCoverage.Entities;
using CodeCoverage.Recipients;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Webhooks.GitHub.Messages;
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

        public Task BroadcastAsync<TMessage>(TMessage message, string queueName, CancellationToken cancellationToken = default)
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
    {
        bus = new RecordingMessageBus();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddSingleton(session);
        services.AddSingleton<MintPlayer.Spark.Messaging.Abstractions.IMessageBus>(bus);
        services.AddScoped<GitHubEventsRecipient>();
        return services.BuildServiceProvider().GetRequiredService<GitHubEventsRecipient>();
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

    private static string PullRequestJson(string headSha, string baseSha, string action = "opened") => $$"""
        {
          "action": "{{action}}",
          {{(action == "synchronize" ? $"\"before\": \"{HeadSha}\", \"after\": \"{headSha}\"," : "")}}
          "number": 42,
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
            "state": "open", "locked": false, "title": "Add a thing",
            "user": {{UserJson}}, "body": null,
            "created_at": "2026-08-18T09:00:00Z", "updated_at": "2026-08-18T09:00:00Z",
            "closed_at": null, "merged_at": null, "merge_commit_sha": null,
            "assignee": null, "assignees": [], "requested_reviewers": [],
            "requested_teams": [], "labels": [], "milestone": null,
            "draft": false, "author_association": "MEMBER", "active_lock_reason": null,
            "merged": false, "mergeable": true, "rebaseable": true, "mergeable_state": "clean",
            "merged_by": null, "comments": 0, "review_comments": 0, "maintainer_can_modify": true,
            "commits": 1, "additions": 1, "deletions": 0, "changed_files": 1,
            "head": {
              "label": "acme:feature/thing", "ref": "feature/thing", "sha": "{{headSha}}",
              "user": {{UserJson}}, "repo": {{RepositoryJson}}
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
}
