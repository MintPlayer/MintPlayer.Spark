using CodeCoverage.Entities;
using CodeCoverage.Forge;
using Xunit;

namespace CodeCoverage.Tests.Model;

/// <summary>
/// Nothing a fork upload writes can land on a document id a first-party upload owns.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>RavenDB has no "insert only".</b> Storing under an existing id overwrites it, silently and
/// completely. A fork upload is driven by a caller with no credential for the repository, and it
/// supplies the sha's pull request and its own run id — so "could this land on someone else's
/// document" is a security question, not a housekeeping one.
/// </para>
/// <para>
/// The whole tree is asserted, not just the commit. Every id below a commit is built by string
/// concatenation from the commit or build id, so the <c>pr/{n}/</c> segment propagates to all of
/// them — but that is a property of today's helpers, and a helper rewritten to compose from
/// <c>(repoId, sha)</c> instead would re-introduce the collision without touching
/// <c>Commit.DocumentId</c> at all.
/// </para>
/// </remarks>
public class ForkDocumentIdCollisionTests
{
    private const long RepoId = 1266490237;
    private const string Sha = "16a31b920f994427d87e493d79821b39316f32bc";
    private const int Pr = 42;
    private const long RunId = 99;
    private const int Attempt = 1;

    private static string FirstPartyCommit => Commit.DocumentId(EForgeProvider.GitHub, RepoId, Sha);
    private static string ForkCommit => Commit.DocumentId(EForgeProvider.GitHub, RepoId, Sha, Pr);

    private static string FirstPartyBuild => Build.DocumentId(EForgeProvider.GitHub, RepoId, Sha, RunId, Attempt);
    private static string ForkBuild => Build.DocumentId(EForgeProvider.GitHub, RepoId, Sha, RunId, Attempt, Pr);

    /// <summary>Every document id either upload writes, for the same repository, sha, run and attempt.</summary>
    private static string[] Tree(string commitId, string buildId) =>
    [
        commitId,
        buildId,
        CommitAssembly.DocumentId(commitId),
        CommitAssembly.FileDocumentId(commitId, "src/a.cs"),
        BuildTreeSummary.DocumentId(buildId),
        BuildTreeSummary.FlagDocumentId(buildId, "unit"),
        FileCoverage.DocumentId(buildId, "src/a.cs"),
        FileCoverage.FlagDocumentId(buildId, "unit", "src/a.cs"),
    ];

    [Fact]
    public void No_document_a_fork_writes_shares_an_id_with_a_first_party_one()
    {
        var firstParty = Tree(FirstPartyCommit, FirstPartyBuild);
        var fork = Tree(ForkCommit, ForkBuild);

        // Same length, so the comparison below is over the same set of concepts.
        Assert.Equal(firstParty.Length, fork.Length);

        var collisions = firstParty.Intersect(fork, StringComparer.Ordinal).ToArray();
        Assert.True(collisions.Length == 0,
            "These ids are produced by BOTH a fork and a first-party upload for the same commit, so "
            + "one would overwrite the other:\n  " + string.Join("\n  ", collisions));
    }

    /// <summary>
    /// Proves the comparison above is capable of failing: the same inputs with no pull-request
    /// number collide on every single id, which is exactly what the segment prevents.
    /// </summary>
    [Fact]
    public void The_comparison_detects_a_collision_when_the_segment_is_absent()
    {
        var a = Tree(FirstPartyCommit, FirstPartyBuild);
        var b = Tree(FirstPartyCommit, FirstPartyBuild);

        Assert.Equal(a.Length, a.Intersect(b, StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// Every id a fork writes sits under the pull request's own namespace, so a prefix delete of
    /// the pull request or the repository still reaches all of it.
    /// </summary>
    /// <remarks>
    /// ⚠️ Retention depends on this: <c>DeleteRepositoryDataRecipient</c> sweeps
    /// <c>Commits/{provider}/{repoId}/</c> by prefix, which only reaches fork data because it nests
    /// underneath rather than living in a sibling collection.
    /// </remarks>
    [Fact]
    public void Everything_a_fork_writes_nests_under_the_pull_request_segment()
    {
        var prefix = $"Commits/github/{RepoId}/pr/{Pr}/";
        var repositoryPrefix = $"Commits/github/{RepoId}/";

        Assert.All(Tree(ForkCommit, ForkBuild), id =>
        {
            Assert.StartsWith(prefix, id, StringComparison.Ordinal);
            Assert.StartsWith(repositoryPrefix, id, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// Two pull requests carrying the same head sha get separate trees rather than sharing one.
    /// </summary>
    /// <remarks>
    /// Real: a contributor closes a pull request and opens another from the same branch. The cost is
    /// duplicated storage, which is the right trade — sharing would let the second pull request's
    /// upload rewrite the first's recorded numbers.
    /// </remarks>
    [Fact]
    public void The_same_sha_on_two_pull_requests_does_not_share_documents()
    {
        var first = Tree(
            Commit.DocumentId(EForgeProvider.GitHub, RepoId, Sha, 42),
            Build.DocumentId(EForgeProvider.GitHub, RepoId, Sha, RunId, Attempt, 42));
        var second = Tree(
            Commit.DocumentId(EForgeProvider.GitHub, RepoId, Sha, 43),
            Build.DocumentId(EForgeProvider.GitHub, RepoId, Sha, RunId, Attempt, 43));

        Assert.Empty(first.Intersect(second, StringComparer.Ordinal));
    }

    /// <summary>
    /// A fork upload cannot reach another repository's namespace, whatever it supplies.
    /// </summary>
    /// <remarks>
    /// The repository id is resolved server-side from the route and never taken from the form, so
    /// this is really asserting that the id is composed from it — a helper that took an
    /// uploader-supplied value would break this.
    /// </remarks>
    [Fact]
    public void A_fork_upload_stays_inside_its_target_repositorys_namespace()
    {
        var other = Commit.DocumentId(EForgeProvider.GitHub, 999, Sha, Pr);

        Assert.DoesNotContain($"/{RepoId}/", other, StringComparison.Ordinal);
        Assert.StartsWith("Commits/github/999/", other, StringComparison.Ordinal);
    }
}
