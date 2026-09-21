namespace CodeCoverage.Entities;

/// <summary>
/// Whether the GitHub App can still see a repository.
/// <para>
/// Deliberately two values and not three: "archived on GitHub" is a separate, orthogonal fact that
/// lives on <see cref="Repository.Archived"/> — an archived repository is still ours and still
/// connected, it just will not receive uploads. What this enum answers is whether we are still
/// entitled to advertise the repository, which is a question about our access, not about its state.
/// </para>
/// </summary>
public enum RepositoryConnection
{
    /// <summary>The App can see the repository, or an upload has proved it alive.</summary>
    Connected = 0,

    /// <summary>
    /// The repository left our reach — transferred away, removed from the installation's selection,
    /// the App uninstalled, or deleted on GitHub. Its data is kept and its URLs keep resolving; it
    /// simply stops being advertised to anyone but its owner.
    /// </summary>
    Disconnected = 1,
}

/// <summary>
/// Why a repository is disconnected. Informational — it decides the wording shown to the owner, and
/// nothing branches on it.
/// </summary>
public static class DisconnectedReasons
{
    /// <summary>Transferred to an owner the App cannot see.</summary>
    /// <remarks>
    /// ⚠️ <b>Never written, and with GitHub it cannot be.</b> A transfer <em>out</em> of an
    /// installation reaches us only as <c>installation_repositories.removed</c>, which is
    /// byte-for-byte what a plain deselection sends — so the code correctly writes
    /// <see cref="RemovedFromInstallation"/> and has no honest way to tell the two apart. Verified
    /// 2026-09-21: no production path assigns this, and none ever has; the only use is one test
    /// fixture.
    /// <para>
    /// It is kept as a statement of intent for a forge whose API <em>does</em> distinguish them, not
    /// as something to reach for on GitHub. Reaching for it there would put a word in front of an
    /// owner that the event cannot support.
    /// </para>
    /// </remarks>
    public const string TransferredAway = "TransferredAway";

    /// <summary>Deselected from the installation's chosen repositories.</summary>
    public const string RemovedFromInstallation = "RemovedFromInstallation";

    /// <summary>
    /// The integration was removed from the owning account — a GitHub App uninstalled, a GitLab
    /// token revoked, a Bitbucket app removed.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Renamed from <c>AppUninstalled</c>, and these are STORED STRINGS</b>, so the rename is
    /// a migration rather than a rename — <c>M_202609230900</c>. "App" is GitHub's word for its
    /// integration; on GitLab and Bitbucket it names nothing, and a reason string is shown to an
    /// owner, so a word that means nothing on their forge is worse than a vague one.
    /// </remarks>
    public const string IntegrationRemoved = "IntegrationRemoved";

    /// <summary>
    /// The integration was suspended on the owning account. Distinct from removed because it is
    /// explicitly temporary — the owner is expected to lift it, and lifting it restores every
    /// repository — so the page can say so rather than inviting someone to delete the data.
    /// </summary>
    /// <remarks>⚠️ Renamed from <c>AppSuspended</c>; see <see cref="IntegrationRemoved"/>.</remarks>
    public const string IntegrationSuspended = "IntegrationSuspended";

    /// <summary>Deleted on the forge. The numeric id can never come back.</summary>
    /// <remarks>⚠️ Renamed from <c>DeletedOnGitHub</c>; see <see cref="IntegrationRemoved"/>.</remarks>
    public const string DeletedOnForge = "DeletedOnForge";
}
