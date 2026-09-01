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
                         };
    }
}
