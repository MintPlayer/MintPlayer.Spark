using Octokit;
using Xunit;

namespace CodeCoverage.Tests.Services;

/// <summary>
/// A contract test against the real GitHub API, opt-in.
/// <para>
/// <see cref="RepositoryResolver"/>'s last resort asks GitHub what an <c>owner/name</c> resolves to
/// today, and that only works because GitHub keeps a redirect for renamed and transferred
/// repositories. Measured 2026-09-05: <c>GET /repos/MintPlayer/CodeCoverage</c> answers <c>301</c>
/// with <c>Location: https://api.github.com/repositories/1305831351</c> — the numeric id is in the
/// redirect target itself — and following it yields <c>MintPlayer-Archive/CodeCoverage</c>.
/// </para>
/// <para>
/// What this test actually pins down is the half we do not control: whether <b>Octokit</b> follows
/// that redirect. If it ever stops, the fallback silently degrades to "unknown name" and renamed
/// repositories quietly stop resolving — a failure that no amount of mocking would reveal, because
/// a mock would be written to the behaviour we assumed.
/// </para>
/// <para>
/// Not run by default: it needs the network, and unauthenticated GitHub allows 60 requests an hour
/// per IP, which CI runners share. Enable with <c>COVERAGE_GITHUB_CONTRACT_TESTS=1</c>.
/// </para>
/// </summary>
public class GitHubRenameRedirectContractTests
{
    private const string EnableVariable = "COVERAGE_GITHUB_CONTRACT_TESTS";

    /// <summary>A repository this organization really did transfer, kept as the fixture.</summary>
    private const string OldOwner = "MintPlayer";
    private const string OldName = "CodeCoverage";
    private const long ExpectedId = 1305831351;
    private const string ExpectedFullName = "MintPlayer-Archive/CodeCoverage";

    /// <remarks>
    /// An early return rather than a skip attribute, to avoid taking a dependency on
    /// Xunit.SkippableFact for one test. It therefore reports as passed when disabled — read the
    /// absence of a failure here as "not run", not as "verified".
    /// </remarks>
    [Fact]
    public async Task Octokit_follows_GitHubs_rename_redirect_and_reports_the_current_repository()
    {
        if (Environment.GetEnvironmentVariable(EnableVariable) != "1")
            return;

        // Anonymous, exactly as a self-hosted instance without App credentials would be.
        var client = new GitHubClient(new ProductHeaderValue("mintplayer-coverage-contract-test"));

        var repository = await client.Repository.Get(OldOwner, OldName);

        Assert.Equal(ExpectedId, repository.Id);
        Assert.Equal(ExpectedFullName, repository.FullName);
    }
}
