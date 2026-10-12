using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CodeCoverage.GitMirror.Mirror;
using CodeCoverage.GitMirror.Tests._Infrastructure;

namespace CodeCoverage.GitMirror.Tests.Mirror;

/// <summary>
/// Clone and fetch through the sidecar's API against a local smart-HTTP origin (plan M1/M2; S1, S4, S7, S8 as automated
/// tests): the D26 recipe, fork isolation, force-pushes, the join, the quotas, the timeout and DELETE.
/// </summary>
public class MirrorServiceTests : MirrorTestBase
{
    private static int Clones(SidecarHost sidecar) => sidecar.Started.Count(p => p.ArgumentList.Contains("clone"));

    [Fact]
    public async Task A_clone_mirrors_heads_and_tags_only_with_the_hardened_config()
    {
        CreateOrigin("repo");

        var response = await Sidecar.FetchAsync("github", "1001", Server.UrlOf("repo"));

        response.Outcome.Should().Be("cloned");
        response.Reason.Should().BeNull();
        response.CommitCount.Should().Be(4);
        response.SizeBytes.Should().BeGreaterThan(0);
        response.RefsHash.Should().HaveLength(64);
        response.RefsHash!.All(char.IsAsciiHexDigitLower).Should().BeTrue();
        var mirror = Sidecar.MirrorOf("github", "1001");
        FixtureGit.Run(mirror, "for-each-ref", "--format=%(refname)").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Should().BeEquivalentTo(["refs/heads/feature", "refs/heads/main", "refs/tags/light", "refs/tags/v1"]);

        var config = FixtureGit.Run(mirror, "config", "--list", "--local");
        config.Should().Contain("remote.origin.tagopt=--no-tags");
        config.Should().Contain("remote.origin.fetch=+refs/heads/*:refs/heads/*");
        config.Should().Contain("remote.origin.fetch=+refs/tags/*:refs/tags/*");
        config.Should().Contain("remote.origin.partialclonefilter=tree:0");
        config.Should().Contain("gc.auto=0");
        config.Should().Contain("maintenance.auto=false");
        config.Should().Contain("log.mailmap=false");
        config.Should().Contain("protocol.file.allow=never");
        FixtureGit.Run(mirror, "config", "--get", "core.hooksPath").Trim().Should().Be(Sidecar.Runner.HooksPath);
        Directory.Exists(Path.Combine(mirror, "objects", "info", "commit-graphs")).Should().BeTrue("the split commit-graph is written explicitly");
        File.Exists(Path.Combine(mirror, "packed-refs")).Should().BeTrue();
    }

    [Fact]
    public async Task A_fetch_brings_new_commits_and_changes_the_refs_hash_only_when_refs_move()
    {
        var shas = CreateOrigin("repo");
        var cloned = await Sidecar.FetchAsync("github", "1002", Server.UrlOf("repo"));

        Push("repo", "main", "e", 5, shas["c"]);
        var fetched = await Sidecar.FetchAsync("github", "1002", Server.UrlOf("repo"));
        var again = await Sidecar.FetchAsync("github", "1002", Server.UrlOf("repo"));

        fetched.Outcome.Should().Be("fetched");
        fetched.CommitCount.Should().Be(5);
        fetched.RefsHash.Should().NotBe(cloned.RefsHash);
        again.Outcome.Should().Be("fetched");
        again.RefsHash.Should().Be(fetched.RefsHash, "a no-op fetch leaves the refs hash alone");
        Clones(Sidecar).Should().Be(1);
    }

    [Fact]
    public async Task A_force_push_and_deleted_refs_reach_the_mirror_and_the_old_snapshot_goes_stale()
    {
        var shas = CreateOrigin("repo");
        await Sidecar.FetchAsync("github", "1003", Server.UrlOf("repo"));
        using var before = await Sidecar.GraphAsync("github", "1003");
        var oldSnapshot = before.RootElement.GetProperty("snapshotId").GetString()!;

        Render(before).Should().Be(
            "d lane=0 colour=0 up=[] down=[0:0]\n" +
            "c lane=1 colour=1 up=[] down=[0:0]\n" +
            "b lane=0 colour=0 up=[0] down=[0:0]\n" +
            "a lane=0 colour=0 up=[0] down=[]\n");

        // Force-push main back to b plus a new commit, delete the feature branch and the annotated tag.
        Push("repo", "main", "e", 6, shas["b"]);
        FixtureGit.Run(Server.PathOf("repo"), "update-ref", "-d", "refs/heads/feature");
        FixtureGit.Run(Server.PathOf("repo"), "update-ref", "-d", "refs/tags/v1");
        var fetched = await Sidecar.FetchAsync("github", "1003", Server.UrlOf("repo"));

        var stale = await Sidecar.Client.GetAsync($"/repos/github/1003/graph?snapshot={oldSnapshot}");
        stale.StatusCode.Should().Be(HttpStatusCode.Gone);
        using var staleBody = JsonDocument.Parse(await stale.Content.ReadAsStringAsync());
        staleBody.RootElement.GetProperty("state").GetString().Should().Be("stale");
        var current = staleBody.RootElement.GetProperty("snapshotId").GetString();
        current.Should().NotBe(oldSnapshot);

        using var after = await Sidecar.GraphAsync("github", "1003", $"?snapshot={current}");
        after.RootElement.GetProperty("refsHash").GetString().Should().Be(fetched.RefsHash);
        Render(after).Should().Be(
            "e lane=0 colour=0 up=[] down=[0:0]\n" +
            "b lane=0 colour=0 up=[0] down=[0:0]\n" +
            "a lane=0 colour=0 up=[0] down=[]\n");
        after.RootElement.GetProperty("refs").EnumerateArray().Select(r => r.GetProperty("name").GetString())
            .Should().BeEquivalentTo(["refs/heads/main", "refs/tags/light"]);
    }

    private static string Render(JsonDocument page)
    {
        var text = new StringBuilder();
        foreach (var row in page.RootElement.GetProperty("rows").EnumerateArray())
        {
            var up = string.Join(',', row.GetProperty("up").EnumerateArray().Select(u => u.GetInt32()));
            var down = string.Join(',', row.GetProperty("down").EnumerateArray().Select(d => $"{d[0].GetInt32()}:{d[1].GetInt32()}"));
            text.Append($"{row.GetProperty("subject").GetString()} lane={row.GetProperty("lane").GetInt32()} colour={row.GetProperty("colour").GetInt32()} up=[{up}] down=[{down}]\n");
        }
        return text.ToString();
    }

    [Fact]
    public async Task Two_concurrent_fetches_of_one_repository_run_one_clone_and_get_the_same_result()
    {
        CreateOrigin("repo");
        Server.Delay = TimeSpan.FromMilliseconds(400); // holds the first clone in flight while the second call arrives

        var first = Sidecar.FetchAsync("github", "1004", Server.UrlOf("repo"));
        var second = Sidecar.FetchAsync("github", "1004", Server.UrlOf("repo"));
        var results = await Task.WhenAll(first, second);

        results[0].Outcome.Should().Be("cloned");
        results[1].Should().Be(results[0], "the second caller joined the first operation (D19), duration included");
        Clones(Sidecar).Should().Be(1);
    }

    [Fact]
    public async Task Control_two_concurrent_fetches_of_different_repositories_run_two_clones()
    {
        CreateOrigin("repo");
        Server.Delay = TimeSpan.FromMilliseconds(400);

        await Task.WhenAll(
            Sidecar.FetchAsync("github", "1005", Server.UrlOf("repo")),
            Sidecar.FetchAsync("github", "1006", Server.UrlOf("repo")));

        Clones(Sidecar).Should().Be(2, "the join is per repository, so the S8 assertion above can fail");
    }

    [Fact]
    public async Task Too_many_refs_is_refused_before_cloning_and_leaves_no_directory()
    {
        CreateOrigin("repo");

        var response = await Sidecar.FetchAsync("github", "1007", Server.UrlOf("repo"), quota: new { maxRefs = 3 });

        response.Outcome.Should().Be("refused");
        response.Reason.Should().Be("too-many-refs");
        Clones(Sidecar).Should().Be(0);
        Entries(Sidecar.Root).Where(e => e.Contains("1007")).Should().BeEmpty();
    }

    [Fact]
    public async Task A_clone_above_the_size_quota_is_refused_and_deleted()
    {
        CreateOrigin("repo");

        var response = await Sidecar.FetchAsync("github", "1008", Server.UrlOf("repo"), quota: new { maxBytes = 1 });

        response.Outcome.Should().Be("refused");
        response.Reason.Should().Be("too-large");
        Clones(Sidecar).Should().Be(1);
        Entries(Sidecar.Root).Where(e => e.Contains("1008")).Should().BeEmpty();
    }

    [Fact]
    public async Task A_clone_that_runs_out_of_time_is_killed_and_leaves_no_directory()
    {
        CreateOrigin("repo");
        Server.Delay = TimeSpan.FromSeconds(8);
        var clock = Stopwatch.StartNew();

        var response = await Sidecar.FetchAsync("github", "1009", Server.UrlOf("repo"), quota: new { timeoutSeconds = 1 });

        response.Outcome.Should().Be("failed");
        response.Reason.Should().Be("timeout");
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(7), "the process tree was killed, not waited for");
        Entries(Sidecar.Root).Where(e => e.Contains("1009")).Should().BeEmpty();
    }

    [Fact]
    public async Task A_missing_origin_fails_as_not_found_and_leaves_no_directory()
    {
        var response = await Sidecar.FetchAsync("github", "1010", Server.UrlOf("absent"));

        response.Outcome.Should().Be("failed");
        response.Reason.Should().Be("not-found");
        Entries(Sidecar.Root).Where(e => e.Contains("1010")).Should().BeEmpty();
    }

    [Fact]
    public async Task Fetch_only_without_a_mirror_is_refused_as_missing_without_starting_git_or_creating_a_directory()
    {
        CreateOrigin("repo");

        var response = await Sidecar.FetchAsync("github", "1013", Server.UrlOf("repo"), mode: "fetch-only");

        response.Outcome.Should().Be("refused");
        response.Reason.Should().Be("missing");
        Sidecar.Started.Should().BeEmpty("D40: only a view may clone, so a missing mirror starts nothing");
        Server.Requests.Should().Be(0);
        Entries(Sidecar.Root).Should().BeEmpty();
    }

    [Fact]
    public async Task Fetch_only_fetches_an_existing_mirror_and_clone_or_fetch_clones_a_missing_one()
    {
        var shas = CreateOrigin("repo");

        var cloned = await Sidecar.FetchAsync("github", "1014", Server.UrlOf("repo"), mode: "clone-or-fetch");
        Push("repo", "main", "e", 5, shas["c"]);
        var fetched = await Sidecar.FetchAsync("github", "1014", Server.UrlOf("repo"), mode: "fetch-only");

        cloned.Outcome.Should().Be("cloned");
        fetched.Outcome.Should().Be("fetched");
        fetched.CommitCount.Should().Be(5);
        Clones(Sidecar).Should().Be(1);
    }

    [Fact]
    public async Task An_unknown_fetch_mode_is_a_bad_request()
    {
        CreateOrigin("repo");

        var response = await Sidecar.Client.PostAsJsonAsync("/repos/github/1015/fetch", new { cloneUrl = Server.UrlOf("repo").AbsoluteUri, mode = "clone" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Sidecar.Started.Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_removes_the_mirror_and_is_idempotent()
    {
        CreateOrigin("repo");
        await Sidecar.FetchAsync("github", "1011", Server.UrlOf("repo"));
        (await Sidecar.GraphAsync("github", "1011")).Dispose(); // a snapshot with an open reader on the mirror
        Directory.Exists(Sidecar.MirrorOf("github", "1011")).Should().BeTrue();

        (await Sidecar.Client.DeleteAsync("/repos/github/1011")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        Directory.Exists(Sidecar.MirrorOf("github", "1011")).Should().BeFalse();
        (await Sidecar.Client.GetAsync("/repos/github/1011/graph")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Sidecar.Client.DeleteAsync("/repos/github/1011")).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Delete_aborts_a_clone_in_flight_and_nothing_is_left()
    {
        CreateOrigin("repo");
        Server.Delay = TimeSpan.FromSeconds(2);

        var fetch = Sidecar.FetchAsync("github", "1012", Server.UrlOf("repo"), quota: new { timeoutSeconds = 60 });
        var deadline = Stopwatch.StartNew();
        while (Clones(Sidecar) == 0 && deadline.Elapsed < TimeSpan.FromSeconds(20))
            await Task.Delay(50);
        Clones(Sidecar).Should().Be(1, "the clone had started");

        (await Sidecar.Client.DeleteAsync("/repos/github/1012")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var response = await fetch;

        response.Outcome.Should().Be("failed");
        Entries(Sidecar.Root).Where(e => e.Contains("1012")).Should().BeEmpty();
    }
}

/// <summary>The free-space floor (D20) is hard: below it nothing is cloned, nor even listed.</summary>
public class DiskFloorTests : MirrorTestBase
{
    protected override IReadOnlyDictionary<string, string?>? SidecarSettings => new Dictionary<string, string?>
    {
        ["GitMirror:MinFreeBytes"] = long.MaxValue.ToString(),
    };

    [Fact]
    public async Task Below_the_floor_a_clone_is_refused_without_starting_git()
    {
        CreateOrigin("repo");

        var response = await Sidecar.FetchAsync("github", "2001", Server.UrlOf("repo"));

        response.Outcome.Should().Be("refused");
        response.Reason.Should().Be("disk-floor");
        Sidecar.Started.Should().BeEmpty();
        Entries(Sidecar.Root).Should().BeEmpty();
    }
}
