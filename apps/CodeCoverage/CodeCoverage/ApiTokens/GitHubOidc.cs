namespace CodeCoverage.ApiTokens;

/// <summary>
/// GitHub Actions OIDC constants. A workflow with `permissions: id-token: write`
/// can mint a GitHub-signed JWT asserting which repository/run it is — verified
/// against GitHub's JWKS, nothing to store or leak (Codecov's tokenless model).
/// </summary>
public static class GitHubOidc
{
    public const string SchemeName = "GitHubOidc";
    public const string Issuer = "https://token.actions.githubusercontent.com";

    // Claim names as GitHub emits them (inbound claim mapping disabled).
    public const string RepositoryClaim = "repository";
    public const string RepositoryIdClaim = "repository_id";
    public const string RepositoryOwnerClaim = "repository_owner";
    public const string RepositoryOwnerIdClaim = "repository_owner_id";
    public const string RepositoryVisibilityClaim = "repository_visibility";
    public const string RunIdClaim = "run_id";
    public const string RunAttemptClaim = "run_attempt";
}
