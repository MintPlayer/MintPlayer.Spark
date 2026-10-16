using CodeCoverage.Entities;

namespace CodeCoverage.Forge;

/// <summary>
/// Who may switch a repository's dependency updates on, as the forge sees it (dependency-updates PRD R20, R21, R32, R33):
/// the repositories of the App's installations the signed-in viewer can reach, each with whether the viewer administers it,
/// and, when an action executes, a fresh check of the same with the App's own credential.
/// </summary>
/// <remarks>
/// <para>
/// <b>A narrow seam of its own</b> (<see cref="EForgeCapability.RepositoryAdminCheck"/>), like <see cref="IForgeMergeQueue"/>:
/// the onboarding page, its query and its custom action ask this, never a forge's API, so a GitLab or Bitbucket
/// implementation is all a second forge needs. GitHub is the only implementation today.
/// </para>
/// <para>
/// <b>Administer means the forge's top role only</b> (R33): GitHub's <c>ADMIN</c>, not <c>MAINTAIN</c>, because the
/// updater pushes branches and merges.
/// </para>
/// <para>
/// <b>Nothing here is a grant.</b> The listing is what the viewer's own credential answers, scoped to the viewer by the
/// forge; an installation key the viewer cannot reach answers empty, exactly like one that does not exist (no existence
/// oracle). The execution-time check is the authority for a forge-admin reason: a listing is shown, never trusted.
/// </para>
/// </remarks>
public interface IForgeRepositoryAdministration
{
    EForgeProvider Provider { get; }

    /// <summary>
    /// The signed-in viewer's own login on this forge, as the forge answers the viewer's own credential (GitHub's
    /// <c>GET /user</c>), never a claim or a name the viewer can edit. Null login when the viewer has no linked identity on
    /// this forge, or its credential is dead or unreachable (<see cref="ForgeViewerLogin.State"/> says which).
    /// </summary>
    Task<ForgeViewerLogin> GetViewerLoginAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The repositories of the App's installations the viewer can reach with their own credential, each with whether the
    /// viewer administers it; only the installation <paramref name="installationKey"/> names when given. An installation
    /// the viewer cannot reach, or one that does not exist, gives no rows and <see cref="EForgeCredentialState.Ok"/>.
    /// </summary>
    Task<ForgeViewerRepositories> ListViewerRepositoriesAsync(string? installationKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// For each of <paramref name="repositories"/> (all of <paramref name="account"/>), whether <paramref name="login"/>
    /// administers it now, asked with the App's own credential, never the viewer's. Keyed by repository document id. A
    /// repository the forge does not answer for is false: fail closed.
    /// </summary>
    Task<IReadOnlyDictionary<string, bool>> CheckAdministratorAsync(Account account, IReadOnlyList<Repository> repositories, string login,
        CancellationToken cancellationToken = default);
}

/// <summary>The viewer's verified login on one forge, or why there is none.</summary>
/// <param name="Linked">The viewer's account has a sign-in of this forge at all.</param>
public sealed record ForgeViewerLogin(string? Login, bool Linked, EForgeCredentialState State);

/// <summary>What <see cref="IForgeRepositoryAdministration.ListViewerRepositoriesAsync"/> found.</summary>
/// <param name="Login">The viewer's verified login the listing was made for; null when there was none to make it for.</param>
public sealed record ForgeViewerRepositories(IReadOnlyList<ForgeViewerRepository> Repositories, string? Login, EForgeCredentialState State)
{
    public static ForgeViewerRepositories None(string? login, EForgeCredentialState state) => new([], login, state);
}

/// <summary>One repository of an installation the viewer can reach.</summary>
/// <param name="InstallationKey">The installation it was listed under, opaque to callers.</param>
/// <param name="AccountForgeId">The forge's id of the owning account (the <c>Account</c> document id derives from it).</param>
/// <param name="RepositoryForgeId">The forge's id of the repository (the <c>Repository</c> document id derives from it).</param>
/// <param name="IsAdmin">The viewer administers it (R33: the top role only), as the listing says. Shown, never trusted.</param>
public sealed record ForgeViewerRepository(string InstallationKey, long AccountForgeId, string AccountLogin, long RepositoryForgeId, string FullName,
    bool IsPrivate, bool IsAdmin);

/// <summary>
/// The forge's way back after an App is installed or its repositories changed (R20, R36): the parameters it puts on the
/// App's setup URL, read neutrally. <see cref="EForgeCapability.SetupRedirect"/>.
/// </summary>
/// <remarks>
/// <b>Never trusted.</b> Anyone can type the URL: the installation key only narrows a listing already filtered to what
/// the viewer manages (GitHub's docs warn the <c>installation_id</c> can be spoofed), and the action is a hint for the page.
/// </remarks>
public interface IForgeSetupRedirect
{
    EForgeProvider Provider { get; }

    /// <summary>The setup parameters in <paramref name="query"/>, or null when they are not this forge's.</summary>
    ForgeSetupRedirect? Read(IReadOnlyDictionary<string, string?> query);
}

/// <param name="InstallationKey">The installation the forge names, as <see cref="IForgeRepositoryAdministration"/> takes it.</param>
public sealed record ForgeSetupRedirect(EForgeProvider Provider, string InstallationKey, EForgeSetupAction Action);

/// <summary>Why the forge sent the viewer back: a hint only (GitHub does not document <c>setup_action</c>, R36).</summary>
public enum EForgeSetupAction
{
    /// <summary>No hint, or one this app does not know.</summary>
    Unknown,

    /// <summary>The App was installed.</summary>
    Install,

    /// <summary>The installation's repositories or permissions changed.</summary>
    Update,

    /// <summary>An organization member asked for the installation; an owner still has to approve it.</summary>
    Request,
}
