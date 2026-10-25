using CodeCoverage.GitMirror.Mirror;
using CodeCoverage.GitMirror.Tests._Infrastructure;

namespace CodeCoverage.GitMirror.Tests.Graph;

/// <summary>
/// Golden layouts (plan M3) of real repositories built with the git CLI: fixed timestamps make date order and every SHA
/// deterministic, so each expectation pins the order git walks AND the JoinLeft lanes (D27) laid over it.
/// Legend: '*' the commit, 'v' a lane converging into it, '|' a lane passing through, '.' a free column; then lane, colour,
/// up and down exactly as the wire carries them. Each expectation was checked by hand against the rules in
/// <see cref="CodeCoverage.GitMirror.Graph.LaneLayout"/>.
/// </summary>
public class LaneLayoutGoldenTests
{
    private static async Task<string> LayoutOf(RepoBuilder builder)
    {
        using var dir = new TempDir("golden");
        var repo = dir.Sub("origin.git");
        builder.WriteTo(repo);
        return await GraphFixtures.RenderAsync(await GraphFixtures.OpenAsync(repo));
    }

    private static string Rows(params string[] rows) => string.Concat(rows.Select(r => r + "\n"));

    [Fact]
    public async Task Linear_history_stays_in_one_lane()
    {
        var layout = await LayoutOf(new RepoBuilder()
            .Commit("a", 1).Commit("b", 2, "a").Commit("c", 3, "b")
            .Branch("main", "c"));

        layout.Should().Be(Rows(
            "*     c      lane=0 colour=0 up=[] down=[0:0]",
            "*     b      lane=0 colour=0 up=[0] down=[0:0]",
            "*     a      lane=0 colour=0 up=[0] down=[]"));
    }

    [Fact]
    public async Task A_merge_opens_a_lane_for_its_second_parent()
    {
        var layout = await LayoutOf(new RepoBuilder()
            .Commit("base", 1).Commit("main1", 2, "base").Commit("feat1", 3, "base").Commit("main2", 4, "main1").Commit("merge", 5, "main2", "feat1")
            .Branch("main", "merge"));

        // main1's first parent is already expected by feat1's lane, but that lane is to the RIGHT: main stays straight
        // and both converge at base (JoinLeft).
        layout.Should().Be(Rows(
            "*.    merge  lane=0 colour=0 up=[] down=[0:0,1:1]",
            "*|    main2  lane=0 colour=0 up=[0] down=[0:0]",
            "|*    feat1  lane=1 colour=1 up=[1] down=[1:1]",
            "*|    main1  lane=0 colour=0 up=[0] down=[0:0]",
            "*v    base   lane=0 colour=0 up=[0,1] down=[]"));
    }

    [Fact]
    public async Task An_octopus_merge_opens_a_lane_per_parent()
    {
        var layout = await LayoutOf(new RepoBuilder()
            .Commit("base", 1).Commit("main1", 2, "base").Commit("x", 3, "base").Commit("y", 4, "base").Commit("oct", 5, "main1", "x", "y")
            .Branch("main", "oct"));

        layout.Should().Be(Rows(
            "*..   oct    lane=0 colour=0 up=[] down=[0:0,1:1,2:2]",
            "||*   y      lane=2 colour=2 up=[2] down=[2:2]",
            "|*|   x      lane=1 colour=1 up=[1] down=[1:1]",
            "*||   main1  lane=0 colour=0 up=[0] down=[0:0]",
            "*vv   base   lane=0 colour=0 up=[0,1,2] down=[]"));
    }

    [Fact]
    public async Task A_criss_cross_merge_bends_only_to_the_left()
    {
        var layout = await LayoutOf(new RepoBuilder()
            .Commit("base", 1).Commit("a1", 2, "base").Commit("b1", 3, "base").Commit("a2", 4, "a1", "b1").Commit("b2", 5, "b1", "a1")
            .Branch("main", "a2").Branch("other", "b2"));

        // a2 is a new tip in column 2; its first parent a1 is already expected by column 1 (to its left), so it joins
        // that lane and column 2 is freed. a1's first parent base is expected by column 0, to the left: it joins too.
        layout.Should().Be(Rows(
            "*.    b2     lane=0 colour=0 up=[] down=[0:0,1:1]",
            "||*   a2     lane=2 colour=2 up=[] down=[1:1,0:0]",
            "*|    b1     lane=0 colour=0 up=[0] down=[0:0]",
            "|*    a1     lane=1 colour=1 up=[1] down=[0:0]",
            "*     base   lane=0 colour=0 up=[0] down=[]"));
    }

    [Fact]
    public async Task A_branch_that_ends_at_its_own_root_frees_its_lane()
    {
        // gh-pages style: an orphan branch whose lane ends at its root g0, plus a short-lived feature off m0.
        var layout = await LayoutOf(new RepoBuilder()
            .Commit("m0", 1).Commit("g0", 2).Commit("m1", 3, "m0").Commit("g1", 4, "g0").Commit("f1", 5, "m0").Commit("m2", 6, "m1")
            .Branch("main", "m2").Branch("pages", "g1").Branch("feature", "f1"));

        layout.Should().Be(Rows(
            "*     m2     lane=0 colour=0 up=[] down=[0:0]",
            "|*    f1     lane=1 colour=1 up=[] down=[1:1]",
            "||*   g1     lane=2 colour=2 up=[] down=[2:2]",
            "*||   m1     lane=0 colour=0 up=[0] down=[0:0]",
            "||*   g0     lane=2 colour=2 up=[2] down=[]",
            "*v    m0     lane=0 colour=0 up=[0,1] down=[]"));
    }

    [Fact]
    public async Task A_tag_on_a_merge_points_its_pill_at_the_merge_commit()
    {
        using var dir = new TempDir("golden");
        var repo = dir.Sub("origin.git");
        var shas = new RepoBuilder()
            .Commit("base", 1).Commit("main1", 2, "base").Commit("feat1", 3, "base").Commit("merge", 4, "main1", "feat1").Commit("after", 5, "merge")
            .Branch("main", "after").Tag("v1.0", "merge", annotated: true).Tag("light", "feat1")
            .WriteTo(repo);
        var snapshot = await GraphFixtures.OpenAsync(repo);

        (await GraphFixtures.RenderAsync(snapshot)).Should().Be(Rows(
            "*     after  lane=0 colour=0 up=[] down=[0:0]",
            "*.    merge  lane=0 colour=0 up=[0] down=[0:0,1:1]",
            "|*    feat1  lane=1 colour=1 up=[1] down=[1:1]",
            "*|    main1  lane=0 colour=0 up=[0] down=[0:0]",
            "*v    base   lane=0 colour=0 up=[0,1] down=[]"));
        // The annotated tag is peeled to the merge commit, so its pill lands on the merge row.
        snapshot.Refs.Should().ContainSingle(r => r.Name == "refs/tags/v1.0" && r.Kind == "tag" && r.Sha == shas["merge"]);
        snapshot.Refs.Should().ContainSingle(r => r.Name == "refs/tags/light" && r.Kind == "tag" && r.Sha == shas["feat1"]);
        snapshot.Refs.Should().ContainSingle(r => r.Name == "refs/heads/main" && r.Kind == "head" && r.Sha == shas["after"]);
    }

    [Fact]
    public async Task A_version_tag_carries_the_run_that_wrote_it_and_other_refs_none()
    {
        using var dir = new TempDir("golden");
        var repo = dir.Sub("origin.git");
        const string run = "https://github.com/acme/widgets/actions/runs/18234567890";
        new RepoBuilder()
            .Commit("base", 1).Commit("main1", 2, "base")
            .Branch("main", "main1")
            // As the versioning engine writes it (#544 D5): a subject, then key: value trailers.
            .Tag("Widgets/v1.2.0", "main1", annotated: true, message: $"Widgets 1.2.0\n\napi-hash: sha256:{new string('a', 64)}\nrun: {run}\n")
            .Tag("Widgets/v1.1.0", "base", annotated: true, message: "Widgets 1.1.0\n\napi-hash: none\n")
            // A lightweight tag pointing at a commit: no tag object, so no trailers to read.
            .Tag("light", "main1")
            .WriteTo(repo);

        var state = await MirrorRefsState.ReadAsync(GraphFixtures.Runner(), repo, CancellationToken.None);

        state!.Refs.Should().ContainSingle(r => r.Name == "refs/tags/Widgets/v1.2.0").Which.Run.Should().Be(run);
        state.Refs.Should().ContainSingle(r => r.Name == "refs/tags/Widgets/v1.1.0").Which.Run.Should().BeNull();
        state.Refs.Should().ContainSingle(r => r.Name == "refs/tags/light").Which.Run.Should().BeNull();
        state.Refs.Should().ContainSingle(r => r.Name == "refs/heads/main").Which.Run.Should().BeNull();
    }

    [Fact]
    public async Task A_child_older_than_its_parent_is_still_shown_above_it()
    {
        // Clock skew: "child" (minute 5) is older than its parent "parent" (minute 10). Date order never shows a parent
        // before all its children, which the streaming layout depends on; "other" (minute 7) sorts by its own time.
        var layout = await LayoutOf(new RepoBuilder()
            .Commit("base", 1).Commit("parent", 10, "base").Commit("child", 5, "parent").Commit("other", 7, "base")
            .Branch("main", "child").Branch("side", "other"));

        layout.Should().Be(Rows(
            "*     other  lane=0 colour=0 up=[] down=[0:0]",
            "|*    child  lane=1 colour=1 up=[] down=[1:1]",
            "|*    parent lane=1 colour=1 up=[1] down=[0:0]",
            "*     base   lane=0 colour=0 up=[0] down=[]"));
    }
}
