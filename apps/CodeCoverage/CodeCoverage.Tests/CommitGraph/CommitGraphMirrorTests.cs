using CodeCoverage.CommitGraph;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using Xunit;

namespace CodeCoverage.Tests.CommitGraph;

/// <summary>
/// D10 and D20 against the in-memory sidecar: mirrors are created only by a read, evicted by age and
/// by budget pressure least-recently-viewed first, capped per installation, and an evicted repository
/// comes back through the same path as a first view.
/// </summary>
public class CommitGraphMirrorTests : CoverageRavenTest
{
    private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    private static string RepoId(long id) => Repository.DocumentId(EForgeProvider.GitHub, id);

    private static async Task<RepositoryGraphState?> StateAsync(Raven.Client.Documents.IDocumentStore store, long id)
    {
        using var session = store.OpenAsyncSession();
        return await session.LoadAsync<RepositoryGraphState>(RepositoryGraphState.ForRepository(RepoId(id)));
    }

    [Fact]
    public async Task A_mirror_not_viewed_for_the_eviction_age_is_deleted_and_a_recent_one_is_kept()
    {
        using var store = GetDocumentStore();
        var kit = new CommitGraphKit(store);
        await CommitGraphKit.SeedRepositoryAsync(store, 1);
        await CommitGraphKit.SeedRepositoryAsync(store, 2);
        await CommitGraphKit.SeedMirrorAsync(store, kit.Sidecar, 1, 100, Now.AddDays(-61));
        await CommitGraphKit.SeedMirrorAsync(store, kit.Sidecar, 2, 100, Now.AddDays(-1));

        var removed = await kit.Get<ICommitGraphMirrors>().EvictIdleAsync();

        removed.Should().Be(1);
        kit.Sidecar.Deletes.Should().Equal("github/1");
        (await StateAsync(store, 1)).Should().BeNull();
        (await StateAsync(store, 2)).Should().NotBeNull();
        using var session = store.OpenAsyncSession();
        (await session.LoadAsync<CommitGraphMetrics>(CommitGraphMetrics.DocumentId))!.Evictions.Should().Be(1);
    }

    [Fact]
    public async Task Under_budget_pressure_the_least_recently_viewed_mirrors_go_first_and_the_new_clone_stays()
    {
        using var store = GetDocumentStore();
        var kit = new CommitGraphKit(store);
        kit.Options.BudgetBytes = 100;
        kit.Options.InstallationShare = 1;
        foreach (var id in new long[] { 1, 2, 3, 4 }) await CommitGraphKit.SeedRepositoryAsync(store, id);
        await CommitGraphKit.SeedMirrorAsync(store, kit.Sidecar, 1, 40, Now.AddDays(-3));
        await CommitGraphKit.SeedMirrorAsync(store, kit.Sidecar, 2, 40, Now.AddDays(-1));
        await CommitGraphKit.SeedMirrorAsync(store, kit.Sidecar, 3, 40, Now.AddDays(-2));
        kit.Sidecar.Sizes["github/4"] = 30;

        // A first view of repository 4 asks for its clone.
        (await kit.Get<ICommitGraphReader>().ReadAsync(await LoadRepositoryAsync(store, 4), new CommitGraphRequest()))
            .State.Should().Be("syncing");
        await kit.DrainAsync();

        // 120 ≥ 100 before the clone: the oldest view (1) goes, leaving 80. The clone adds 30 → 110 > 100,
        // so the next oldest (3) goes. 2, viewed most recently, and the new clone stay.
        kit.Sidecar.Deletes.Should().Equal("github/1", "github/3");
        kit.Sidecar.Fetches.Should().Equal("github/4");
        (await StateAsync(store, 2)).Should().NotBeNull();
        (await StateAsync(store, 4))!.HasMirror.Should().BeTrue();
        using var session = store.OpenAsyncSession();
        (await session.LoadAsync<CommitGraphMetrics>(CommitGraphMetrics.DocumentId))!.Evictions.Should().Be(2);
    }

    [Fact]
    public async Task A_capped_installation_is_refused_while_another_installation_still_clones()
    {
        using var store = GetDocumentStore();
        var kit = new CommitGraphKit(store);
        kit.Options.BudgetBytes = 1000;
        kit.Options.InstallationShare = 0.25; // 250 bytes per installation
        foreach (var id in new long[] { 1, 2, 10, 20 }) await CommitGraphKit.SeedRepositoryAsync(store, id);
        await CommitGraphKit.SeedMirrorAsync(store, kit.Sidecar, 1, 150, Now, installation: "github:1");
        await CommitGraphKit.SeedMirrorAsync(store, kit.Sidecar, 2, 150, Now, installation: "github:1");
        kit.Forge.GitAccess[RepoId(10)] = new ForgeGitAccess("https://forge.example.invalid/acme/repo10.git", "x-access-token", "not-a-real-token", "github:1");
        kit.Forge.GitAccess[RepoId(20)] = new ForgeGitAccess("https://forge.example.invalid/acme/repo20.git", "x-access-token", "not-a-real-token", "github:2");

        var reader = kit.Get<ICommitGraphReader>();
        await reader.ReadAsync(await LoadRepositoryAsync(store, 10), new CommitGraphRequest());
        await reader.ReadAsync(await LoadRepositoryAsync(store, 20), new CommitGraphRequest());
        await kit.DrainAsync();

        kit.Sidecar.Fetches.Should().Equal("github/20");
        var refused = await StateAsync(store, 10);
        refused!.HasMirror.Should().BeFalse();
        refused.Outcome.Should().Be("refused");
        refused.Reason.Should().Be("installation-cap");
        (await StateAsync(store, 20))!.HasMirror.Should().BeTrue();

        // The refusal is what the panel shows, and a read does not hammer the cap with retries.
        kit.Time.Now = kit.Time.Now.AddMinutes(2);
        var again = await kit.Get<ICommitGraphReader>().ReadAsync(await LoadRepositoryAsync(store, 10), new CommitGraphRequest());
        again.State.Should().Be("unavailable");
        again.Reason.Should().Be("installation-cap");
    }

    [Fact]
    public async Task An_evicted_repository_is_cloned_again_on_its_next_view()
    {
        using var store = GetDocumentStore();
        var kit = new CommitGraphKit(store);
        await CommitGraphKit.SeedRepositoryAsync(store, 1);
        await CommitGraphKit.SeedMirrorAsync(store, kit.Sidecar, 1, 100, Now.AddDays(-90));
        kit.Sidecar.Pages["github/1"] = FakeSidecar.Page(("1111111111111111111111111111111111111111", null));

        await kit.Get<ICommitGraphMirrors>().EvictIdleAsync();
        kit.Sidecar.Mirrors.Should().BeEmpty();

        var repository = await LoadRepositoryAsync(store, 1);
        (await kit.Get<ICommitGraphReader>().ReadAsync(repository, new CommitGraphRequest())).State.Should().Be("syncing");
        kit.Bus.Of<FetchCommitGraphMessage>().Should().ContainSingle().Which.AllowClone.Should().BeTrue();
        await kit.DrainAsync();

        var page = await kit.Get<ICommitGraphReader>().ReadAsync(repository, new CommitGraphRequest());
        page.State.Should().Be("ready");
        page.Rows.Should().ContainSingle();
        kit.Sidecar.Fetches.Should().Equal("github/1");
    }

    [Fact]
    public async Task Only_a_read_creates_a_mirror_a_refresh_of_a_repository_without_one_does_nothing()
    {
        using var store = GetDocumentStore();
        var kit = new CommitGraphKit(store);
        await CommitGraphKit.SeedRepositoryAsync(store, 1);

        await kit.Get<ICommitGraphMirrors>().FetchAsync(RepoId(1), allowClone: false);
        await kit.Get<ICommitGraphMirrors>().FetchAsync(RepoId(1), allowClone: true);

        // No state document: nobody viewed it, so neither a push nor even a clone request acts.
        kit.Sidecar.Requests.Should().BeEmpty();
        (await StateAsync(store, 1)).Should().BeNull();
    }

    [Fact]
    public async Task The_hourly_safety_net_refreshes_only_existing_mirrors_with_an_old_fetch()
    {
        using var store = GetDocumentStore();
        var kit = new CommitGraphKit(store);
        foreach (var id in new long[] { 1, 2, 3 }) await CommitGraphKit.SeedRepositoryAsync(store, id);
        await CommitGraphKit.SeedMirrorAsync(store, kit.Sidecar, 1, 10, Now, fetchedAtUtc: Now.AddHours(-3));
        await CommitGraphKit.SeedMirrorAsync(store, kit.Sidecar, 2, 10, Now, fetchedAtUtc: Now.AddMinutes(-5));
        using (var session = store.OpenAsyncSession())
        {
            // Viewed, but its clone was refused: no mirror to refresh.
            await session.StoreAsync(new RepositoryGraphState { Repository = RepoId(3), Outcome = "refused", LastFetchAtUtc = Now.AddDays(-1) },
                RepositoryGraphState.ForRepository(RepoId(3)));
            await session.SaveChangesAsync();
        }

        (await kit.Get<ICommitGraphMirrors>().QueueRefreshesAsync()).Should().Be(1);
        var message = kit.Bus.Of<FetchCommitGraphMessage>().Should().ContainSingle().Which;
        message.RepositoryId.Should().Be(RepoId(1));
        message.AllowClone.Should().BeFalse();
        kit.Bus.DeduplicationKeys.Should().ContainSingle().Which.Should().Contain(":hourly:");
    }

    [Theory]
    [InlineData("too-large")]
    [InlineData("too-many-refs")]
    public async Task A_mirror_that_outgrew_its_quota_on_a_fetch_is_recorded_as_gone_and_not_recloned_hourly(string reason)
    {
        using var store = GetDocumentStore();
        var kit = new CommitGraphKit(store);
        await CommitGraphKit.SeedRepositoryAsync(store, 1);
        await CommitGraphKit.SeedMirrorAsync(store, kit.Sidecar, 1, 100, Now, fetchedAtUtc: Now.AddHours(-3));
        // The sidecar's contract: a fetch that pushes an existing mirror over a quota removes it and answers refused.
        kit.Sidecar.OnFetch = mirror =>
        {
            kit.Sidecar.Mirrors.Remove(mirror);
            return new SidecarFetchResult("refused", reason, 0, null, 0, 1_000_000, 5);
        };

        await kit.Get<ICommitGraphMirrors>().FetchAsync(RepoId(1), allowClone: false);

        var state = (await StateAsync(store, 1))!;
        state.HasMirror.Should().BeFalse("the sidecar removed the mirror, so nothing is left to refresh or count");
        state.SizeBytes.Should().Be(0);
        state.Outcome.Should().Be("refused");
        state.Reason.Should().Be(reason);

        // Otherwise the hourly net would ask for a fetch, which the sidecar runs as a full clone of a mirror it no
        // longer has, only to refuse it again: every hour, for ever.
        kit.Time.Now = kit.Time.Now.AddHours(2);
        (await kit.Get<ICommitGraphMirrors>().QueueRefreshesAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_fetch_refused_at_the_disk_floor_keeps_the_existing_mirror()
    {
        using var store = GetDocumentStore();
        var kit = new CommitGraphKit(store);
        await CommitGraphKit.SeedRepositoryAsync(store, 1);
        await CommitGraphKit.SeedMirrorAsync(store, kit.Sidecar, 1, 100, Now, fetchedAtUtc: Now.AddHours(-3));
        kit.Sidecar.OnFetch = _ => new SidecarFetchResult("refused", "disk-floor", 0, null, 0, 1, 5);

        await kit.Get<ICommitGraphMirrors>().FetchAsync(RepoId(1), allowClone: false);

        var state = (await StateAsync(store, 1))!;
        state.HasMirror.Should().BeTrue("the floor refuses the fetch before touching the mirror");
        state.SizeBytes.Should().Be(100);
    }

    [Theory]
    [InlineData("push")]
    [InlineData("hourly")]
    public async Task A_refresh_of_a_mirror_the_sidecar_lost_marks_the_state_mirror_less_and_only_a_later_view_clones_it(string trigger)
    {
        using var store = GetDocumentStore();
        var kit = new CommitGraphKit(store);
        await CommitGraphKit.SeedRepositoryAsync(store, 1);
        await CommitGraphKit.SeedMirrorAsync(store, kit.Sidecar, 1, 100, Now, fetchedAtUtc: Now.AddHours(-3));
        kit.Sidecar.Pages["github/1"] = FakeSidecar.Page(("1111111111111111111111111111111111111111", null));
        kit.Sidecar.Mirrors.Clear(); // the volume was lost; the app still believes in its mirror

        if (trigger == "hourly")
            (await kit.Get<ICommitGraphMirrors>().QueueRefreshesAsync()).Should().Be(1);
        else
            await kit.Bus.BroadcastAsync(new FetchCommitGraphMessage { RepositoryId = RepoId(1), AllowClone = false });
        await kit.DrainAsync();

        // D40: the non-view trigger asked fetch-only, so the sidecar cloned nothing and said so.
        kit.Sidecar.FetchBodies.Should().ContainSingle().Which.Should().Contain("\"mode\":\"fetch-only\"");
        kit.Sidecar.Mirrors.Should().BeEmpty();
        var state = (await StateAsync(store, 1))!;
        state.HasMirror.Should().BeFalse();
        state.SizeBytes.Should().Be(0);
        state.Reason.Should().Be("missing");
        kit.Bus.Of<FetchCommitGraphMessage>().Should().BeEmpty("nothing queues a clone but a view");
        using (var session = store.OpenAsyncSession())
            (await session.LoadAsync<CommitGraphMetrics>(CommitGraphMetrics.DocumentId)).Should().BeNull("a missing mirror is not a refusal");

        // Nor does the next hourly net: there is no mirror to refresh.
        kit.Time.Now = kit.Time.Now.AddHours(2);
        (await kit.Get<ICommitGraphMirrors>().QueueRefreshesAsync()).Should().Be(0);

        // The next view clones it lazily, at once (the "missing" outcome is no refusal to wait out).
        var repository = await LoadRepositoryAsync(store, 1);
        (await kit.Get<ICommitGraphReader>().ReadAsync(repository, new CommitGraphRequest())).State.Should().Be("syncing");
        kit.Bus.Of<FetchCommitGraphMessage>().Should().ContainSingle().Which.AllowClone.Should().BeTrue();
        await kit.DrainAsync();

        kit.Sidecar.FetchBodies.Should().HaveCount(2);
        kit.Sidecar.FetchBodies[1].Should().Contain("\"mode\":\"clone-or-fetch\"");
        (await StateAsync(store, 1))!.HasMirror.Should().BeTrue();
        (await kit.Get<ICommitGraphReader>().ReadAsync(repository, new CommitGraphRequest())).State.Should().Be("ready");
    }

    [Fact]
    public async Task A_fetch_hands_the_credential_to_the_sidecar_and_stores_none_of_it()
    {
        using var store = GetDocumentStore();
        var kit = new CommitGraphKit(store);
        await CommitGraphKit.SeedRepositoryAsync(store, 1, isPrivate: true);
        const string token = "ghs_fixture_token_value";
        kit.Forge.GitAccess[RepoId(1)] = new ForgeGitAccess("https://forge.example.invalid/acme/repo1.git", "x-access-token", token, "github:7");

        await kit.Get<ICommitGraphReader>().ReadAsync(await LoadRepositoryAsync(store, 1), new CommitGraphRequest());
        await kit.DrainAsync();

        kit.FetchBodiesContain(token).Should().BeTrue("the sidecar needs the token for this one fetch");
        using var session = store.OpenAsyncSession();
        var state = await session.LoadAsync<RepositoryGraphState>(RepositoryGraphState.ForRepository(RepoId(1)));
        state!.InstallationId.Should().Be("github:7");
        var raw = System.Text.Json.JsonSerializer.Serialize(state);
        raw.Contains(token, StringComparison.Ordinal).Should().BeFalse("no credential is ever stored");
        kit.Forge.GitAccess[RepoId(1)]!.ToString().Contains(token, StringComparison.Ordinal).Should().BeFalse("a logged access record must not print the token");
    }

    private static async Task<Repository> LoadRepositoryAsync(Raven.Client.Documents.IDocumentStore store, long id)
    {
        using var session = store.OpenAsyncSession();
        return (await session.LoadAsync<Repository>(RepoId(id)))!;
    }
}

internal static class CommitGraphKitAssertions
{
    /// <summary>Whether any fetch body carried <paramref name="value"/>, without printing either.</summary>
    public static bool FetchBodiesContain(this CommitGraphKit kit, string value)
        => kit.Sidecar.FetchBodies.Any(b => b.Contains(value, StringComparison.Ordinal));
}
