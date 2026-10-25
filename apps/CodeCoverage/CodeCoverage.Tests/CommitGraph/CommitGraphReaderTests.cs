using System.Text.Json;
using CodeCoverage.CommitGraph;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests.CommitGraph;

/// <summary>
/// The graph read for a visible repository (#504, PRD §4.4, D23, D28): the coverage overlay is one
/// load and no query, emails never leave the server, avatars cost no forge call when they can be
/// parsed or are cached, and the sidecar's 410 reaches the client as <c>stale</c>.
/// </summary>
public class CommitGraphReaderTests : CoverageRavenTest
{
    private const long RepoGitHubId = 42;
    private static readonly string RepoId = Repository.DocumentId(EForgeProvider.GitHub, RepoGitHubId);
    private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    private const string ShaA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ShaB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string ShaC = "cccccccccccccccccccccccccccccccccccccccc";

    // Fixture identities only: an id-bearing no-reply, an old login no-reply, and two plain addresses.
    private const string NoReplyWithId = "1234567+test-author@users.noreply.github.com";
    private const string NoReplyLogin = "test-author@users.noreply.github.com";
    private const string Known = "author@example.invalid";
    private const string Unknown = "nobody@example.invalid";

    private static async Task<(CommitGraphKit Kit, Repository Repository)> SetUpAsync(IDocumentStore store, params (string Sha, string? Email)[] rows)
    {
        var kit = new CommitGraphKit(store);
        await CommitGraphKit.SeedRepositoryAsync(store, RepoGitHubId);
        await CommitGraphKit.SeedMirrorAsync(store, kit.Sidecar, RepoGitHubId, 10, Now, fetchedAtUtc: Now);
        kit.Sidecar.Pages[$"github/{RepoGitHubId}"] = FakeSidecar.Page(rows);
        using var session = store.OpenAsyncSession();
        return (kit, (await session.LoadAsync<Repository>(RepoId))!);
    }

    [Fact]
    public async Task The_coverage_overlay_is_one_load_and_no_query()
    {
        using var store = GetDocumentStore();
        var (kit, repository) = await SetUpAsync(store, (ShaA, null), (ShaB, null), (ShaC, null));
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new Commit
            {
                Sha = ShaA, Repository = RepoId,
                Coverage = new CoverageSummary { LinesCovered = 3, LinesCoverable = 4 },
                CoverageDeltaVsParent = -1.5, AssemblyCompleteness = "Partial",
            }, Commit.DocumentId(EForgeProvider.GitHub, RepoGitHubId, ShaA));
            // A record without a report yet: pending, not "no report".
            await seed.StoreAsync(new Commit { Sha = ShaB, Repository = RepoId }, Commit.DocumentId(EForgeProvider.GitHub, RepoGitHubId, ShaB));
            await seed.SaveChangesAsync();
        }

        var queries = 0;
        store.OnBeforeQuery += (_, _) => Interlocked.Increment(ref queries);

        var page = await kit.Get<ICommitGraphReader>().ReadAsync(repository, new CommitGraphRequest());

        queries.Should().Be(0, "commit ids are deterministic, so the overlay needs no RQL at all (PRD §4.4, S6)");
        page.State.Should().Be("ready");
        page.Rows.Select(r => r.Sha).Should().Equal(ShaA, ShaB, ShaC);
        var a = page.Rows[0].Coverage!;
        a.Percent.Should().Be(75);
        a.DeltaVsParent.Should().Be(-1.5);
        a.Completeness.Should().Be("Partial");
        page.Rows[1].Coverage.Should().NotBeNull();
        page.Rows[1].Coverage!.LinesCovered.HasValue.Should().BeFalse();
        page.Rows[2].Coverage.Should().BeNull();
        page.Rows[0].Refs.Should().Equal("refs/heads/main");
        page.SnapshotId.Should().Be("snap-1");
    }

    [Fact]
    public async Task A_commit_names_the_latest_release_run_of_its_own_repository_only()
    {
        using var store = GetDocumentStore();
        var (kit, repository) = await SetUpAsync(store, (ShaA, null), (ShaB, null), (ShaC, null));
        var runs = $"https://github.com/{repository.FullName}/actions/runs/";
        var page = FakeSidecar.Page((ShaA, null), (ShaB, null), (ShaC, null));
        kit.Sidecar.Pages[$"github/{RepoGitHubId}"] = page with
        {
            Refs =
            [
                .. page.Refs,
                // Two packages tagged by two attempts' runs: the newest run is the one to show.
                new SidecarRef("refs/tags/Widgets/v1.2.0", "tag", ShaA, runs + "100"),
                new SidecarRef("refs/tags/Widgets.Core/v1.2.0", "tag", ShaA, runs + "101"),
                // Anyone who can push a tag can write the line: another repository's run is never looked up.
                new SidecarRef("refs/tags/Widgets/v1.1.0", "tag", ShaB, "https://github.com/evil/elsewhere/actions/runs/7"),
                new SidecarRef("refs/tags/Widgets/v1.0.0", "tag", ShaC, "not a url"),
            ],
        };

        var read = await kit.Get<ICommitGraphReader>().ReadAsync(repository, new CommitGraphRequest());

        read.Rows.Select(r => r.ReleaseRun).Should().Equal(101L, null, null);
    }

    [Theory]
    [InlineData("https://github.com/acme/widgets/actions/runs/42", 42L)]
    [InlineData("https://GitHub.com/ACME/Widgets/actions/runs/42", 42L)]
    [InlineData("https://github.com/acme/widgets-two/actions/runs/42", null)]
    [InlineData("https://github.example/acme/widgets/actions/runs/42", null)]
    [InlineData("https://github.com/acme/widgets/actions/runs/0", null)]
    [InlineData(null, null)]
    public void A_run_url_names_a_run_only_of_the_same_github_repository(string? run, long? expected)
    {
        var repository = new Repository { Provider = EForgeProvider.GitHub, FullName = "acme/widgets" };
        CommitGraphReader.ReleaseRunId(repository, run).Should().Be(expected);
        CommitGraphReader.ReleaseRunId(new Repository { Provider = EForgeProvider.GitLab, FullName = "acme/widgets" }, run).HasValue.Should().BeFalse();
    }

    [Fact]
    public async Task No_email_reaches_the_response_and_no_reply_authors_cost_no_forge_call()
    {
        using var store = GetDocumentStore();
        var (kit, repository) = await SetUpAsync(store,
            (ShaA, NoReplyWithId), (ShaB, NoReplyLogin), (ShaC, Known), ("dddddddddddddddddddddddddddddddddddddddd", Unknown));
        kit.Avatars.Answers[Known] = "https://avatars.githubusercontent.com/u/99?v=4";

        var page = await kit.Get<ICommitGraphReader>().ReadAsync(repository, new CommitGraphRequest());

        var json = JsonSerializer.Serialize(page, JsonSerializerOptions.Web);
        json.Should().NotContain("@", "no email, and nothing shaped like one, may reach a browser (D28)");
        json.Should().NotContain("example.invalid");
        json.Should().NotContain("noreply");

        // The forge is asked once, and only about the two authors that could not be parsed.
        kit.Avatars.Calls.Should().ContainSingle();
        kit.Avatars.Calls[0].Select(l => l.Email).Should().BeEquivalentTo([Known, Unknown]);

        page.Rows[0].AvatarKey.Should().NotBeNull();
        page.Rows[1].AvatarKey.Should().NotBeNull();
        page.Rows[2].AvatarKey.Should().NotBeNull();
        page.Rows[3].AvatarKey.Should().BeNull("an author the forge does not know gets initials");
        page.Rows[0].AvatarKey.Should().MatchRegex("^github-[0-9a-f]{64}$");
    }

    [Fact]
    public async Task A_page_of_only_no_reply_authors_makes_no_forge_call_and_a_second_view_makes_none_either()
    {
        using var store = GetDocumentStore();
        var (kit, repository) = await SetUpAsync(store, (ShaA, NoReplyWithId), (ShaB, Known), (ShaC, Unknown));
        kit.Avatars.Answers[Known] = "https://avatars.githubusercontent.com/u/99?v=4";

        await kit.Get<ICommitGraphReader>().ReadAsync(repository, new CommitGraphRequest());
        kit.Avatars.Calls.Should().ContainSingle();

        // Second view: every author is cached, the miss as well (negative entry).
        kit.Time.Now = kit.Time.Now.AddMinutes(5);
        var second = await kit.Get<ICommitGraphReader>().ReadAsync(repository, new CommitGraphRequest());
        kit.Avatars.Calls.Should().ContainSingle("a second view is served from the cache, misses included");
        second.Rows[1].AvatarKey.Should().NotBeNull();
        second.Rows[2].AvatarKey.Should().BeNull();

        // A page with only a no-reply author never asks.
        kit.Sidecar.Pages[$"github/{RepoGitHubId}"] = FakeSidecar.Page(("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", "7654321+someone-else@users.noreply.github.com"));
        var third = await kit.Get<ICommitGraphReader>().ReadAsync(repository, new CommitGraphRequest());
        kit.Avatars.Calls.Should().ContainSingle();
        third.Rows[0].AvatarKey.Should().NotBeNull();
    }

    [Fact]
    public async Task The_avatar_image_is_fetched_once_from_the_allowed_host_and_then_served_from_the_cache()
    {
        using var store = GetDocumentStore();
        var (kit, repository) = await SetUpAsync(store, (ShaA, NoReplyWithId));
        var key = (await kit.Get<ICommitGraphReader>().ReadAsync(repository, new CommitGraphRequest())).Rows[0].AvatarKey!;

        var first = await kit.Get<ICommitGraphAvatars>().GetImageAsync(key);
        var second = await kit.Get<ICommitGraphAvatars>().GetImageAsync(key);

        first!.ContentType.Should().Be("image/png");
        second!.Bytes.Should().Equal(first.Bytes);
        kit.Images.Requests.Should().ContainSingle().Which.ToString().Should().Be("https://avatars.githubusercontent.com/u/1234567?v=4");

        (await kit.Get<ICommitGraphAvatars>().GetImageAsync("github-" + new string('0', 64))).Should().BeNull("an unknown key");
        (await kit.Get<ICommitGraphAvatars>().GetImageAsync("../../etc")).Should().BeNull("not a key");
    }

    [Fact]
    public async Task An_svg_avatar_is_refused_because_it_can_carry_script()
    {
        using var store = GetDocumentStore();
        var (kit, repository) = await SetUpAsync(store, (ShaA, NoReplyWithId));
        kit.Images.ContentType = "image/svg+xml";
        var key = (await kit.Get<ICommitGraphReader>().ReadAsync(repository, new CommitGraphRequest())).Rows[0].AvatarKey!;

        (await kit.Get<ICommitGraphAvatars>().GetImageAsync(key)).Should().BeNull();
    }

    [Fact]
    public async Task A_moved_snapshot_answers_stale_with_the_current_snapshot_id()
    {
        using var store = GetDocumentStore();
        var (kit, repository) = await SetUpAsync(store, (ShaA, null));

        var page = await kit.Get<ICommitGraphReader>().ReadAsync(repository, new CommitGraphRequest(Snapshot: "an-old-snapshot"));

        page.State.Should().Be("stale");
        page.SnapshotId.Should().Be("snap-1");
        page.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task A_stale_mirror_serves_its_rows_and_queues_a_refresh_that_cannot_clone()
    {
        using var store = GetDocumentStore();
        var (kit, repository) = await SetUpAsync(store, (ShaA, null));
        kit.Time.Now = kit.Time.Now.AddHours(1);

        var page = await kit.Get<ICommitGraphReader>().ReadAsync(repository, new CommitGraphRequest(Count: 500));

        page.State.Should().Be("ready");
        page.Rows.Should().ContainSingle();
        var fetch = kit.Bus.Of<FetchCommitGraphMessage>().Should().ContainSingle().Which;
        fetch.AllowClone.Should().BeFalse();
        kit.Sidecar.GraphReads.Should().ContainSingle().Which.Should().Contain("count=200", "count is capped at 200");
    }

    [Fact]
    public async Task A_read_stamps_the_view_for_the_eviction_order()
    {
        using var store = GetDocumentStore();
        var (kit, repository) = await SetUpAsync(store, (ShaA, null));
        kit.Time.Now = kit.Time.Now.AddDays(3);

        await kit.Get<ICommitGraphReader>().ReadAsync(repository, new CommitGraphRequest());

        using var session = store.OpenAsyncSession();
        (await session.LoadAsync<RepositoryGraphState>(RepositoryGraphState.ForRepository(RepoId)))!
            .LastViewedAtUtc.Should().Be(kit.Time.Now.UtcDateTime);
    }

    [Fact]
    public async Task A_clone_asked_for_twice_in_one_window_is_queued_both_times()
    {
        // The bus drops a repeated key, as the real one does. The first view clones; the mirror is then
        // lost (a wiped volume) a minute later, inside the same view window. The second clone request
        // must reach the queue, or the state says "syncing" with nothing behind it until the lost bound.
        using var store = GetDocumentStore();
        var kit = new CommitGraphKit(store);
        kit.Bus.Deduplicates = true;
        await CommitGraphKit.SeedRepositoryAsync(store, RepoGitHubId);
        kit.Sidecar.Pages[$"github/{RepoGitHubId}"] = FakeSidecar.Page((ShaA, null));
        Repository repository;
        using (var session = store.OpenAsyncSession())
            repository = (await session.LoadAsync<Repository>(RepoId))!;
        var reader = () => kit.Get<ICommitGraphReader>().ReadAsync(repository, new CommitGraphRequest());

        (await reader()).State.Should().Be("syncing");
        await kit.DrainAsync();
        (await reader()).State.Should().Be("ready");

        kit.Sidecar.Mirrors.Remove($"github/{RepoGitHubId}");
        kit.Time.Now = kit.Time.Now.AddMinutes(1);
        (await reader()).State.Should().Be("syncing");
        kit.Bus.Of<FetchCommitGraphMessage>().Should().ContainSingle().Which.AllowClone.Should().BeTrue();

        // A second viewer while the clone is under way queues nothing more.
        (await reader()).State.Should().Be("syncing");
        kit.Bus.Of<FetchCommitGraphMessage>().Should().ContainSingle();

        await kit.DrainAsync();
        (await reader()).State.Should().Be("ready");
    }

    [Fact]
    public async Task Without_a_configured_sidecar_the_graph_is_unavailable_and_nothing_is_queued()
    {
        using var store = GetDocumentStore();
        var kit = new CommitGraphKit(store);
        kit.Options.SidecarUrl = null;
        await CommitGraphKit.SeedRepositoryAsync(store, RepoGitHubId);
        using var session = store.OpenAsyncSession();
        var repository = (await session.LoadAsync<Repository>(RepoId))!;

        var page = await kit.Get<ICommitGraphReader>().ReadAsync(repository, new CommitGraphRequest());

        page.State.Should().Be("unavailable");
        page.Reason.Should().Be("disabled");
        kit.Bus.Messages.Should().BeEmpty();
        kit.Sidecar.Requests.Should().BeEmpty();
    }
}
