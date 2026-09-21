using CodeCoverage.Entities;
using Raven.Client.Documents.Indexes;

namespace CodeCoverage.Indexes;

/// <summary>
/// Commit lists per repository, newest first, optionally only commits that
/// have coverage. Query through <see cref="Result"/> (the indexed fields),
/// then OfType back to the Commit documents. AuthoredAt is coalesced with
/// FirstSeenAtUtc so upload-only commits (which never get a webhook timestamp)
/// still sort chronologically instead of clustering at one end.
/// </summary>
public class Commits_ByRepository : AbstractIndexCreationTask<Commit>
{
    public class Result
    {
        public string? Repository { get; set; }
        public string? Branch { get; set; }
        public DateTimeOffset? AuthoredAt { get; set; }
        public bool HasCoverage { get; set; }
        public int? PullRequestNumber { get; set; }
        public string? ParentSha { get; set; }
        public bool ParentLookupDone { get; set; }
        /// <summary>Coverage present and the assembly complete (or predating assemblies, i.e. a full upload).</summary>
        public bool CompleteCoverage { get; set; }

        /// <summary>
        /// Contributed from a fork by a caller holding no credential for this repository.
        /// </summary>
        /// <remarks>
        /// ⚠️ Indexed so that branch-keyed reads can exclude these, because they share the
        /// repository field with first-party commits and are otherwise indistinguishable to every
        /// query. The pull-request-keyed reads deliberately do <b>not</b> exclude them — a fork's
        /// coverage appearing on its own pull request is the entire feature.
        /// <para>
        /// ⚠️ <b>Every consumer must test <c>!= true</c>, never <c>!x</c> or <c>== false</c>.</b>
        /// The C# property is a non-nullable bool, which makes it tempting to assume an old document
        /// reads as false — but the index map runs over the stored JSON, and every commit written
        /// before this field existed simply has no such property. An absent field does not satisfy
        /// an equality in RavenDB, so <c>!ContributedFromFork</c> matches <b>none</b> of them.
        /// </para>
        /// <para>
        /// This was shipped wrong and caught in a browser, not by a test: the commit list, history
        /// chart, sparklines, branch list and branch badge all went empty for every pre-existing
        /// repository, while the whole .NET suite stayed green — because every fixture writes the
        /// field. Same class of bug as the <c>!= Disconnected</c> note on
        /// <c>RepositoryVisibility.ListingFilter</c>, which is where the warning already existed.
        /// </para>
        /// </remarks>
        public bool ContributedFromFork { get; set; }
    }

    public Commits_ByRepository()
    {
        Map = commits => from commit in commits
                         select new Result
                         {
                             Repository = commit.Repository,
                             Branch = commit.Branch,
                             AuthoredAt = commit.AuthoredAt ?? commit.FirstSeenAtUtc,
                             HasCoverage = commit.Coverage != null,
                             PullRequestNumber = commit.PullRequestNumber,
                             ParentSha = commit.ParentSha,
                             ParentLookupDone = commit.ParentLookupAttemptedAtUtc != null,
                             CompleteCoverage = commit.Coverage != null
                                 && (commit.AssemblyCompleteness == null || commit.AssemblyCompleteness == "Complete"),
                             ContributedFromFork = commit.ContributedFromFork,
                         };
    }
}
