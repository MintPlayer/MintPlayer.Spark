using CodeCoverage.Forge;
using MintPlayer.SourceGenerators.Attributes;

namespace CodeCoverage.Services;

/// <summary>
/// The GitHub implementation of <see cref="IForgeAccessService"/>.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately thin. The GitHub-specific machinery — the App installation list, the OAuth token
/// refresh dance, the installation-id backfill — stays in <see cref="IGitHubAccessService"/>,
/// because all of it <em>is</em> GitHub-specific and has no counterpart on the other forges.
/// GitLab has no installation concept at all. This class is the seam where that specificity stops:
/// above it, callers see owners and a credential state and nothing else.
/// </para>
/// <para>
/// The mapping it performs is the interesting part. The inner service answers in bare login
/// strings, which is safe only while one forge exists; here each is stamped with
/// <see cref="EForgeProvider.GitHub"/> so that downstream comparisons cannot match an
/// identically-named account on another forge.
/// </para>
/// </remarks>
[Register(typeof(IForgeAccessService), ServiceLifetime.Scoped)]
public partial class GitHubForgeAccessService : IForgeAccessService
{
    [Inject] private readonly IGitHubAccessService gitHubAccess;

    public EForgeProvider Provider => EForgeProvider.GitHub;

    public async Task<ForgeVisibility> GetVisibilityAsync(CancellationToken cancellationToken = default)
    {
        var visibility = await gitHubAccess.GetVisibilityAsync(cancellationToken);
        return new ForgeVisibility(Qualify(visibility.Owners), MapState(visibility.TokenState));
    }

    public async Task<ForgeOwner[]> GetAllowedOwnersAsync(CancellationToken cancellationToken = default)
        => Qualify(await gitHubAccess.GetAllowedOwnersAsync(cancellationToken));

    public async Task<bool> IsOwnerAllowedAsync(ForgeOwner owner, CancellationToken cancellationToken = default)
    {
        // A different forge's owner is not "denied" so much as not ours to answer for; saying no is
        // the only safe answer, and reaching this at all means a caller picked the wrong service.
        if (owner.Provider != Provider)
            return false;

        return await gitHubAccess.IsOwnerAllowedAsync(owner.Login, cancellationToken);
    }

    public Task InvalidateAsync(CancellationToken cancellationToken = default)
        => gitHubAccess.InvalidateAsync(cancellationToken);

    private static ForgeOwner[] Qualify(string[] logins)
        => [.. logins.Select(login => new ForgeOwner(EForgeProvider.GitHub, login))];

    /// <summary>
    /// Maps GitHub's token state onto the provider-neutral one. Written as an exhaustive switch
    /// with a throwing default rather than a silent fallback: a new state that nobody mapped should
    /// be loud, because the quiet failure here would be reporting a dead credential as healthy.
    /// </summary>
    private static EForgeCredentialState MapState(GitHubTokenState state) => state switch
    {
        GitHubTokenState.Ok => EForgeCredentialState.Ok,
        GitHubTokenState.ReauthRequired => EForgeCredentialState.ReauthRequired,
        GitHubTokenState.Unavailable => EForgeCredentialState.Unavailable,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unmapped GitHub token state."),
    };
}
