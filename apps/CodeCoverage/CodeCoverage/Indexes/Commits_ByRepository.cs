using CodeCoverage.Entities;
using MintPlayer.Spark;
using MintPlayer.Spark.Abstractions;
using Raven.Client.Documents.Indexes;

namespace CodeCoverage.Indexes;

/// <summary>
/// The queryable shape of a commit: what the grid, the badges and the history chart read.
/// </summary>
/// <remarks>
/// ⚠️ <b>Only fields with no document counterpart are stored</b> — see the constructor. Everything
/// else, <see cref="Date"/> included, is read back from the document. That is not an optimisation: a
/// stored <see cref="DateTimeOffset"/> is flattened to its UTC instant and its offset is destroyed,
/// which is why <c>CommitIndexShapeGuardTests</c> fails the build if <c>StoreAllFields</c> ever
/// reaches this index. Measured: unstored gives back <c>+02:00</c>, <c>-05:00</c> and <c>+05:30</c>
/// exactly, stored gives back <c>…Z</c>.
/// </remarks>
[FromIndex(typeof(Commits_ByRepository))]
public partial class VCommit
{
    public string? Id { get; set; }

    [Reference(typeof(Repository))]
    public string? Repository { get; set; }

    public string Sha { get; set; } = string.Empty;

    public string? Branch { get; set; }

    /// <summary>
    /// <c>AuthoredAt</c> coalesced with <c>FirstSeenAtUtc</c>, so an upload-only commit — which never
    /// gets a webhook timestamp — still sorts chronologically instead of clustering at one end.
    /// </summary>
    /// <remarks>
    /// The generated <c>DateRaw</c> wrapper is what recovers this for a document written before
    /// <c>Commit.Date</c> existed: such a document has no <c>Date</c> field, so the projection reads
    /// null from it and the stored wrapper is the only surviving copy.
    /// </remarks>
    public DateTimeOffset? Date { get; set; }

    public int? PullRequestNumber { get; set; }

    public string? PullRequestBaseRef { get; set; }

    public string? PullRequestBaseSha { get; set; }

    public string? ParentSha { get; set; }

    /// <summary>
    /// Contributed from a fork by a caller holding no credential for this repository.
    /// </summary>
    /// <remarks>
    /// ⚠️ Indexed so that branch-keyed reads can exclude these, because they share the repository
    /// field with first-party commits and are otherwise indistinguishable to every query. The
    /// pull-request-keyed reads deliberately do <b>not</b> exclude them — a fork's coverage appearing
    /// on its own pull request is the entire feature.
    /// <para>
    /// ⚠️ <b>Every consumer must test <c>!= true</c>, never <c>!x</c> or <c>== false</c>.</b> The C#
    /// property is a non-nullable bool, which makes it tempting to assume an old document reads as
    /// false — but the index map runs over the stored JSON, and every commit written before this field
    /// existed simply has no such property. An absent field does not satisfy an equality in RavenDB,
    /// so <c>!ContributedFromFork</c> matches <b>none</b> of them.
    /// </para>
    /// <para>
    /// This was shipped wrong and caught in a browser, not by a test: the commit list, history chart,
    /// sparklines, branch list and branch badge all went empty for every pre-existing repository,
    /// while the whole .NET suite stayed green — because every fixture writes the field. The same
    /// absent-versus-null asymmetry is pinned framework-side by <c>AbsentVersusNullFieldTests</c>.
    /// </para>
    /// </remarks>
    public bool ContributedFromFork { get; set; }

    public double? CoverageDeltaVsParent { get; set; }

    public double? CoverageDeltaVsDefaultBranch { get; set; }

    /// <summary>
    /// Read back from the document; deliberately <b>not</b> in the map.
    /// </summary>
    /// <remarks>
    /// ⚠️ A complex object in the map must be declared <c>FieldIndexing.No</c> <em>and</em> stored, or
    /// Corax fails the whole index at map time — <c>state=Error, entries=0</c>, every query 500s,
    /// after a deploy that looked clean. Leaving it unmapped yields the same projected value from the
    /// document with none of that.
    /// <para>
    /// ⚠️ Because it is unmapped, <c>Commit.json</c> declares <c>canSort</c>, <c>canFilter</c> and
    /// <c>canListDistincts</c> false for it. <c>IsBackedByShape</c> cannot catch this: it checks the
    /// CLR shape, which is a <em>superset</em> of the map, so it would report the column sortable and
    /// the grid would draw an arrow that 500s.
    /// </para>
    /// </remarks>
    public CoverageSummary? Coverage { get; set; }

    [IgnoreProperty]
    public bool HasCoverage { get; set; }

    [IgnoreProperty]
    public bool ParentLookupDone { get; set; }

    [IgnoreProperty]
    public bool CompleteCoverage { get; set; }
}

/// <summary>
/// Commit lists per repository, newest first, optionally only commits that have coverage.
/// </summary>
/// <remarks>
/// Queried either through <see cref="VCommit"/> (the indexed fields) or with <c>OfType&lt;Commit&gt;()</c>
/// back to the documents — the latter is what every hand-written C# consumer does, and it returns
/// documents with their offsets intact.
/// </remarks>
public partial class Commits_ByRepository : SparkIndexCreationTask<Commit>
{
    public Commits_ByRepository()
    {
        Map = commits => from commit in commits
                         select new VCommit
                         {
                             Id = commit.Id,
                             Repository = commit.Repository,
                             Sha = commit.Sha,
                             Branch = commit.Branch,
                             Date = commit.AuthoredAt ?? commit.FirstSeenAtUtc,
                             DateRaw = new SparkIndexValue<DateTimeOffset?> { V = commit.AuthoredAt ?? commit.FirstSeenAtUtc },
                             PullRequestNumber = commit.PullRequestNumber,
                             PullRequestBaseRef = commit.PullRequestBaseRef,
                             PullRequestBaseSha = commit.PullRequestBaseSha,
                             ParentSha = commit.ParentSha,
                             ContributedFromFork = commit.ContributedFromFork,
                             CoverageDeltaVsParent = commit.CoverageDeltaVsParent,
                             CoverageDeltaVsDefaultBranch = commit.CoverageDeltaVsDefaultBranch,
                             HasCoverage = commit.Coverage != null,
                             ParentLookupDone = commit.ParentLookupAttemptedAtUtc != null,
                             CompleteCoverage = commit.Coverage != null
                                 && (commit.AssemblyCompleteness == null || commit.AssemblyCompleteness == "Complete"),
                         };

        // ⚠️ Per-field, NEVER StoreAllFields. A projection resolves per field — stored fields come
        // from the index, unstored ones from the document — so storing only what the document cannot
        // answer for is what keeps Date's offset. These three are computed in the map and exist
        // nowhere else; without storing them they project as null.
        //
        // DateRaw is stored by the generated ConfigureSparkFields(), not here, because it is the
        // generator that declares it FieldIndexing.No and a field that is neither indexed nor stored
        // fails the index outright.
        Store(nameof(VCommit.HasCoverage), FieldStorage.Yes);
        Store(nameof(VCommit.ParentLookupDone), FieldStorage.Yes);
        Store(nameof(VCommit.CompleteCoverage), FieldStorage.Yes);
    }
}
