namespace CodeCoverage.Forge;

/// <summary>
/// How one forge's CI identity token names the things we need from it.
/// </summary>
/// <remarks>
/// <para>
/// Every forge can mint a signed token asserting which repository and run a job belongs to, and
/// every forge spells those claims differently. The <em>shape</em> is portable; the vocabulary is
/// not — so this is a map rather than an interface, and adding a forge is adding a profile.
/// </para>
/// <para>
/// ⚠️ <b>This is not a capability in the <see cref="EForgeCapability"/> sense.</b> A capability is
/// constant per implementation, and CI identity is not: GitHub mints a token for an ordinary run
/// and <em>never</em> for a pull request from a fork. So availability is decided per request by
/// whether a token arrived at all, not by asking the forge in advance — which is exactly why D6f
/// had to find a credential-less path rather than declaring a capability.
/// </para>
/// <para>
/// <b>The trust decision stays per forge and is not expressed here.</b> A profile says what the
/// claims are called; it does not say that a token is to be believed. Issuer and signing keys are
/// registered at startup by whoever owns that forge, so a profile alone can never authorise
/// anything.
/// </para>
/// </remarks>
/// <param name="Provider">Which forge this profile describes.</param>
/// <param name="SchemeName">The authentication scheme its tokens authenticate under.</param>
/// <param name="Issuer">The expected <c>iss</c>, and where the signing keys are published.</param>
/// <param name="RepositoryClaim">Full name of the repository the job belongs to (<c>owner/name</c> on GitHub; a slash-separated project path on GitLab, which nests).</param>
/// <param name="RepositoryIdClaim">The forge's stable numeric id for that repository.</param>
/// <param name="OwnerClaim">The owning account's login or namespace.</param>
/// <param name="OwnerIdClaim">The owning account's stable numeric id.</param>
/// <param name="VisibilityClaim">The repository's visibility.</param>
/// <param name="PublicVisibilityValue">The value <paramref name="VisibilityClaim"/> takes when the repository is public. ⚠️ Compared exactly: auto-provisioning is gated on it, so a forge whose wording differs must say so rather than rely on the word "public".</param>
/// <param name="RunIdClaim">The CI run this job belongs to.</param>
/// <param name="RunAttemptClaim">
/// Which attempt of that run, where the forge has the concept. Null when it does not — GitLab
/// retries a job rather than re-running an attempt of a pipeline, so there is nothing honest to map.
/// </param>
public sealed record ForgeOidcProfile(
    EForgeProvider Provider,
    string SchemeName,
    string Issuer,
    string RepositoryClaim,
    string RepositoryIdClaim,
    string OwnerClaim,
    string OwnerIdClaim,
    string VisibilityClaim,
    string PublicVisibilityValue,
    string RunIdClaim,
    string? RunAttemptClaim)
{
    /// <summary>
    /// GitHub Actions. A workflow with <c>permissions: id-token: write</c> mints a GitHub-signed
    /// JWT asserting which repository and run it is — verified against GitHub's JWKS, with nothing
    /// stored and nothing to leak.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Never available to a pull request from a fork.</b> GitHub downgrades
    /// <c>id-token: write</c> to read for those runs, so the request variable is simply absent.
    /// That is a platform decision, not a configuration gap, and it is the whole reason fork
    /// uploads needed a different answer (D6f).
    /// </remarks>
    public static ForgeOidcProfile GitHub { get; } = new(
        Provider: EForgeProvider.GitHub,
        SchemeName: "GitHubOidc",
        Issuer: "https://token.actions.githubusercontent.com",
        RepositoryClaim: "repository",
        RepositoryIdClaim: "repository_id",
        OwnerClaim: "repository_owner",
        OwnerIdClaim: "repository_owner_id",
        VisibilityClaim: "repository_visibility",
        PublicVisibilityValue: "public",
        RunIdClaim: "run_id",
        RunAttemptClaim: "run_attempt");

    /// <summary>
    /// GitLab CI, shaped but <b>not registered</b> — stage 2 wires it up. Written now because the
    /// mapping is what proves the profile is a real abstraction rather than GitHub's constants
    /// wearing a record.
    /// </summary>
    /// <remarks>
    /// ⚠️ Two things do not map, and both matter:
    /// <list type="bullet">
    /// <item><description>
    /// There is no run <em>attempt</em>, so <c>RunAttemptClaim</c> is null and a build id derived
    /// from (run, attempt) needs a different second component on GitLab.
    /// </description></item>
    /// <item><description>
    /// <c>project_path</c> on a fork merge request asserts the <b>fork</b>, not the target project —
    /// so unlike GitHub, a GitLab fork <em>can</em> mint a token, but it proves the wrong thing for
    /// authorising a write to the upstream project.
    /// </description></item>
    /// </list>
    /// </remarks>
    public static ForgeOidcProfile GitLab { get; } = new(
        Provider: EForgeProvider.GitLab,
        SchemeName: "GitLabOidc",
        Issuer: "https://gitlab.com",
        RepositoryClaim: "project_path",
        RepositoryIdClaim: "project_id",
        OwnerClaim: "namespace_path",
        OwnerIdClaim: "namespace_id",
        VisibilityClaim: "project_visibility",
        PublicVisibilityValue: "public",
        RunIdClaim: "pipeline_id",
        RunAttemptClaim: null);
}
