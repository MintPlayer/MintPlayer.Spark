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
        /// Mapped as <c>!= true</c> rather than <c>== false</c> would be, had it been nullable:
        /// the field is a non-nullable bool, so a document written before it existed indexes as
        /// false, which is the correct answer for every one of them.
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
