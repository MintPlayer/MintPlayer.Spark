using CodeCoverage.Services;

namespace CodeCoverage.Tests;

/// <summary>
/// A GitHub visibility answer fixed by the test rather than fetched.
/// <para>
/// The real service asks GitHub which accounts the caller may administer, caches it, and — this is
/// the part worth imitating — <b>degrades to the caller's own login on any failure</b>. Controller
/// tests care about what a controller does with the answer, not about how it was obtained, so this
/// lets a test state the answer directly and, importantly, state the *degraded* answer, which is
/// the case real code most often gets wrong.
/// </para>
/// </summary>
public sealed class TestGitHubAccessService(params string[] allowedOwners) : IGitHubAccessService
{
    /// <summary>Owners this caller may administer. Empty models a fully degraded visibility.</summary>
    public string[] AllowedOwners { get; set; } = allowedOwners;

    /// <summary>Counts calls, so a test can assert a controller checked authorization at all.</summary>
    public int IsOwnerAllowedCalls { get; private set; }

    public Task<string[]> GetAllowedOwnersAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(AllowedOwners);

    public Task<bool> IsOwnerAllowedAsync(string ownerLogin, CancellationToken cancellationToken = default)
    {
        IsOwnerAllowedCalls++;
        return Task.FromResult(AllowedOwners.Contains(ownerLogin, StringComparer.OrdinalIgnoreCase));
    }

    public Task<GitHubVisibility> GetVisibilityAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new GitHubVisibility(AllowedOwners, GitHubTokenState.Ok));

    /// <summary>Owners the caller MANAGES (R39); null (the default) means every one of <see cref="AllowedOwners"/>.</summary>
    public string[]? ManagedOwners { get; set; }

    public Task<bool> ManagesOwnerAsync(string ownerLogin, CancellationToken cancellationToken = default)
        => Task.FromResult(AllowedOwners.Contains(ownerLogin, StringComparer.OrdinalIgnoreCase)
            && (ManagedOwners ?? AllowedOwners).Contains(ownerLogin, StringComparer.OrdinalIgnoreCase));

    public Task InvalidateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>The caller's own GitHub login as GitHub verified it; null models a caller with no linked GitHub sign-in.</summary>
    public string? VerifiedLogin { get; set; }

    public Task<GitHubVerifiedLogin> GetVerifiedLoginAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new GitHubVerifiedLogin(VerifiedLogin, VerifiedLogin is not null, GitHubTokenState.Ok));
}
