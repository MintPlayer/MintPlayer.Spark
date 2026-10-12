using CodeCoverage.GitMirror.Graph;
using CodeCoverage.GitMirror.Tests._Infrastructure;

namespace CodeCoverage.GitMirror.Tests.Graph;

/// <summary>
/// The lazy snapshot (S5, plan M3 "done when"): extending page by page, in any order, with the reader closed and resumed
/// with --skip in between, gives exactly the rows and lane state of one full pass; and every page's <c>top</c> is the
/// state a client derives from the rows above it (D27: pass-throughs are not on the wire).
/// </summary>
public class SnapshotPagingTests : IClassFixture<SnapshotPagingTests.RandomDag>
{
    /// <summary>A seeded random DAG of 1,300 commits: branches off recent commits, merges, octopus merges, orphan roots,
    /// skewed clocks, branches and (annotated) tags. Large enough for two checkpoints (every 500 rows).</summary>
    public sealed class RandomDag : IDisposable
    {
        private readonly TempDir dir = new("dag");

        public RandomDag()
        {
            Repository = dir.Sub("origin.git");
            var rng = new Random(504);
            var builder = new RepoBuilder();
            var all = new List<string> { "c0" };
            var tips = new List<string> { "c0" };
            builder.Commit("c0", 0);
            long minute = 0;
            for (var i = 1; i < 1300; i++)
            {
                var name = $"c{i}";
                minute += 1 + rng.Next(3);
                var time = rng.Next(20) == 0 ? minute - rng.Next(30) : minute;
                var roll = rng.Next(100);
                if (roll < 8 && tips.Count < 12)
                {
                    builder.Commit(name, time, all[rng.Next(Math.Max(0, all.Count - 60), all.Count)]);
                    tips.Add(name);
                }
                else if (roll < 18 && tips.Count >= 3)
                {
                    var into = rng.Next(tips.Count);
                    var others = Enumerable.Range(0, tips.Count).Where(t => t != into).OrderBy(_ => rng.Next()).Take(roll < 11 ? 2 : 1).ToList();
                    builder.Commit(name, time, [tips[into], .. others.Select(o => tips[o])]);
                    tips = tips.Where((_, t) => t != into && !others.Contains(t)).Append(name).ToList();
                }
                else if (roll == 99)
                {
                    builder.Commit(name, time);
                    tips.Add(name);
                }
                else
                {
                    var t = rng.Next(tips.Count);
                    builder.Commit(name, time, tips[t]);
                    tips[t] = name;
                }
                all.Add(name);
            }
            for (var t = 0; t < tips.Count; t++)
                builder.Branch($"b{t}", tips[t]);
            for (var k = 0; k < 10; k++)
                builder.Tag($"v{k}", all[rng.Next(all.Count)], annotated: k % 2 == 0);
            builder.WriteTo(Repository);
        }

        public string Repository { get; }

        public void Dispose() => dir.Dispose();
    }

    private readonly RandomDag dag;

    public SnapshotPagingTests(RandomDag dag) => this.dag = dag;

    private sealed record Row(string Sha, int Lane, int Colour, string Up, string Down);

    private static Row RowOf(GraphRow r) => new(r.Sha, r.Lane, r.Colour, string.Join(',', r.Up), string.Join(',', r.Down.Select(d => $"{d[0]}:{d[1]}")));

    private static async Task<List<Row>> FullPassAsync(CommitGraphSnapshot snapshot)
    {
        await snapshot.EnsureRowsAsync(int.MaxValue, CancellationToken.None);
        return GraphService.BuildPage(snapshot, 0, snapshot.Count).Rows.Select(RowOf).ToList();
    }

    /// <summary>The client's derivation (D27): the lanes at row i+1's top are row i's top minus its <c>up</c> plus its
    /// <c>down</c>.</summary>
    private static List<string> DerivedTops(IReadOnlyList<GraphRow> rows)
    {
        var tops = new List<string>();
        var lanes = new SortedDictionary<int, int>();
        foreach (var row in rows)
        {
            tops.Add(string.Join(' ', lanes.Select(l => $"{l.Key}:{l.Value}")));
            foreach (var u in row.Up)
                lanes.Remove(u);
            foreach (var d in row.Down)
                lanes[d[0]] = d[1];
        }
        tops.Add(string.Join(' ', lanes.Select(l => $"{l.Key}:{l.Value}")));
        return tops;
    }

    private static string TopOf(GraphPage page) => string.Join(' ', page.Top.Select(t => $"{t[0]}:{t[1]}"));

    [Fact]
    public async Task Page_by_page_extension_equals_one_full_pass()
    {
        var full = await GraphFixtures.OpenAsync(dag.Repository, repoId: "full");
        var expected = await FullPassAsync(full);
        var tops = DerivedTops(GraphService.BuildPage(full, 0, full.Count).Rows);
        expected.Should().HaveCount(1300);
        full.TotalRows.Should().Be(1300);
        // Not a vacuous comparison: most rows have several lanes open above them, merges converge, columns get reused.
        tops.Count(t => t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 3).Should().BeGreaterThan(500);
        expected.Count(r => r.Up.Contains(',')).Should().BeGreaterThan(25);

        var paged = await GraphFixtures.OpenAsync(dag.Repository, repoId: "paged");
        var actual = new List<Row>();
        var sizes = new[] { 1, 37, 200, 113, 7, 200 };
        for (var (from, i) = (0, 0); from < 1300; i++)
        {
            var count = sizes[i % sizes.Length];
            await paged.EnsureRowsAsync(from + count, CancellationToken.None);
            var page = GraphService.BuildPage(paged, from, count);
            TopOf(page).Should().Be(tops[from], $"the top of the page at {from}");
            page.LaneCount.Should().BeGreaterThan(0);
            actual.AddRange(page.Rows.Select(RowOf));
            from += count;
            if (i % 3 == 2)
                await paged.CloseReaderAsync(); // the next extension resumes with --skip
        }

        actual.Should().Equal(expected);
        paged.Complete.Should().BeTrue();
    }

    [Fact]
    public async Task A_page_far_down_first_then_the_top_matches_the_full_pass_and_its_top_lanes()
    {
        var full = await GraphFixtures.OpenAsync(dag.Repository, repoId: "full2");
        var expected = await FullPassAsync(full);
        var tops = DerivedTops(GraphService.BuildPage(full, 0, full.Count).Rows);

        var jumped = await GraphFixtures.OpenAsync(dag.Repository, repoId: "jump");
        foreach (var from in new[] { 1100, 0, 499, 500, 501, 1299, 777 })
        {
            await jumped.EnsureRowsAsync(from + 50, CancellationToken.None);
            var page = GraphService.BuildPage(jumped, from, 50);
            page.Rows.Select(RowOf).Should().Equal(expected.Skip(from).Take(50));
            TopOf(page).Should().Be(tops[from], $"the top of the page at {from} (checkpoint {from / CommitGraphSnapshot.CheckpointInterval} replayed)");
        }
    }

    [Fact]
    public async Task Find_extends_to_a_commit_far_down_and_reports_its_row()
    {
        var full = await GraphFixtures.OpenAsync(dag.Repository, repoId: "full3");
        var expected = await FullPassAsync(full);

        var snapshot = await GraphFixtures.OpenAsync(dag.Repository, repoId: "find");
        (await snapshot.FindAsync(expected[1234].Sha, CancellationToken.None)).Should().Be(1234);
        snapshot.Count.Should().Be(1235, "the walk stops at the commit it was asked for");
        (await snapshot.FindAsync(new string('0', 40), CancellationToken.None)).Should().Be(-1);
    }

    [Fact]
    public async Task A_first_parent_walk_draws_only_first_parent_edges()
    {
        var snapshot = await GraphFixtures.OpenAsync(dag.Repository, new GraphFilter(null, true, null, null), repoId: "fp");
        await snapshot.EnsureRowsAsync(int.MaxValue, CancellationToken.None);
        var page = GraphService.BuildPage(snapshot, 0, snapshot.Count);

        page.TotalRows.Should().Be(snapshot.Count);
        page.Rows.Should().OnlyContain(r => r.Down.Length <= 1);
        page.Rows.Should().Contain(r => r.Parents.Count > 1, "merges still list all their parents");
        // Every lane ends: no first-parent edge waits for a commit the walk never shows.
        DerivedTops(page.Rows)[^1].Should().BeEmpty();
    }

    [Fact]
    public async Task A_search_is_a_flat_list_with_a_matching_total()
    {
        var snapshot = await GraphFixtures.OpenAsync(dag.Repository, new GraphFilter(null, false, "C12", null), repoId: "grep");
        await snapshot.EnsureRowsAsync(int.MaxValue, CancellationToken.None);
        var page = GraphService.BuildPage(snapshot, 0, snapshot.Count);

        // Subjects are c0..c1299: "c12" (case-insensitive, fixed string) matches c12, c120-c129 and c1200-c1299.
        page.Rows.Select(r => r.Subject).Should().OnlyContain(s => s.StartsWith("c12"));
        page.TotalRows.Should().Be(111);
        page.Rows.Should().HaveCount(111);
        page.Rows.Should().OnlyContain(r => r.Lane == 0 && r.Up.Length == 0 && r.Down.Length == 0);
    }
}
