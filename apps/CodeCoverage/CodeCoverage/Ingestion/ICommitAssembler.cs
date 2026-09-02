using CodeCoverage.Entities;

namespace CodeCoverage.Ingestion;

public interface ICommitAssembler
{
    /// <summary>
    /// Rebuilds the commit's assembly from its finalized builds and the base,
    /// stamps the commit's headline and deltas, and applies the repository
    /// promotion rule. Returns null when the commit has no finalized build.
    /// Caller owns SaveChanges.
    /// </summary>
    Task<CommitAssembly?> AssembleAsync(string commitId, CancellationToken cancellationToken = default);
}
