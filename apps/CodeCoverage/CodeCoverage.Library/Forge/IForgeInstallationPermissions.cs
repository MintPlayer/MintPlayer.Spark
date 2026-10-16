using CodeCoverage.Entities;

namespace CodeCoverage.Forge;

/// <summary>
/// Permissions the app now asks for that the account has not accepted yet (merge-queue PRD O17), and what the app itself
/// fails to ask for that the merge queue needs (§0 D95).
/// </summary>
/// <remarks>
/// A capability of its own rather than a member of <see cref="IForgeIntegration"/>: only GitHub has an
/// install-then-accept-upgrades step, and a forge without one simply registers no implementation.
/// Callers pick the implementation by <see cref="Provider"/>.
/// </remarks>
public interface IForgeInstallationPermissions
{
    EForgeProvider Provider { get; }

    /// <summary>
    /// The pending permissions of the account's installation, or <c>null</c> when nothing can be said:
    /// the account has no installation, the app is not configured, or the forge could not be reached.
    /// Never throws for a forge failure; the page that shows the notice must keep working without it.
    /// </summary>
    Task<PendingInstallationPermissions?> GetPendingAsync(Account account, CancellationToken cancellationToken = default);

    /// <summary>
    /// What the app itself does not declare of what <paramref name="needs"/> takes (§0 D95): permissions it does not
    /// request at the level needed, and events it is not subscribed to. No installation can accept those; only the app's
    /// owner can add them. <c>null</c> when nothing can be said (unconfigured, or the forge could not be read): callers
    /// fail open. Never throws for a forge failure.
    /// </summary>
    Task<ForgeAppGaps?> GetAppGapsAsync(EMergeQueueAppNeed needs, CancellationToken cancellationToken = default)
        => Task.FromResult<ForgeAppGaps?>(null);

    /// <summary>
    /// The members of <paramref name="pending"/> that <paramref name="needs"/> takes at a level the installation has not
    /// accepted yet (§0 D95): the queue cannot work until someone on the account accepts them. A pending permission
    /// the queue does not need, or one already accepted at the level it needs, is not listed.
    /// </summary>
    IReadOnlyList<PendingPermission> NeededPending(IReadOnlyList<PendingPermission> pending, EMergeQueueAppNeed needs) => [];
}

/// <summary>What the merge queue will do on a repository, and so what the app must declare for it (§0 D95).</summary>
[Flags]
public enum EMergeQueueAppNeed
{
    None = 0,

    /// <summary>Rebase, push, publish its check, read rules, reviews and statuses, merge: every queue.</summary>
    Core = 1,

    /// <summary>Dispatch the regenerate workflow and follow its run (<c>postRebaseActionName</c>).</summary>
    Dispatch = 2,

    /// <summary>Re-run failed CI jobs (<c>checks.retryFlaky</c> above 0).</summary>
    RerunActions = 4,

    /// <summary>
    /// Push a commit that touches a workflow file (§0 D59). Only some runs do, so the queue warns about it rather than
    /// refusing, and the installation notice does not ask for it.
    /// </summary>
    Workflows = 8,

    /// <summary>
    /// Close the issues a merged pull request links when the forge left them open (merge-queue PRD §0 D106). Best effort
    /// after the merge, so the queue warns about it rather than refusing; the installation notice does ask for it.
    /// </summary>
    CloseIssues = 16,

    /// <summary>
    /// The dependency updater (dependency-updates PRD §7.2, R15, R16): its own branches and pull request, the lookup, apply and
    /// migrate dispatches, comments and reactions, its check run, and <c>workflows: write</c> for the onboarding pull request
    /// and Actions <c>uses:</c> bumps. It hears its runs finish, comments, checks and the producers' releases.
    /// </summary>
    DependencyUpdates = 32,
}

/// <summary>What the app itself lacks of what was asked about (§0 D95).</summary>
/// <param name="SettingsUrl">
/// The app's own "Permissions &amp; events" settings page, where its owner adds what is missing; <c>null</c> when it could
/// not be named. Not the installation page: accepting there cannot add what the app does not request.
/// </param>
/// <param name="MissingPermissions">Permissions the app does not request at the level needed; <see cref="PendingPermission.Declared"/> is the level needed, <see cref="PendingPermission.Accepted"/> what the app requests (null: nothing).</param>
/// <param name="MissingEvents">Webhook events the app is not subscribed to, as the forge names them (<c>workflow_run</c>).</param>
public sealed record ForgeAppGaps(string? SettingsUrl, IReadOnlyList<PendingPermission> MissingPermissions, IReadOnlyList<string> MissingEvents)
{
    public bool IsEmpty => MissingPermissions.Count == 0 && MissingEvents.Count == 0;
}

/// <summary>The installation's page on the forge, and what still waits for acceptance there.</summary>
/// <param name="HtmlUrl">
/// The installation's own settings page as the forge reports it (<c>html_url</c>), where the account
/// accepts the new permissions. Never built from the login: user and organization installations live
/// under different paths.
/// </param>
/// <param name="MissingFromApp">
/// Permissions the merge queue needs that the app itself does not request (§0 D95): accepting cannot add them.
/// </param>
/// <param name="MissingEvents">Events the merge queue needs that the app is not subscribed to (§0 D95).</param>
/// <param name="AppSettingsUrl">The app's own settings page, where its owner adds the missing ones (§0 D95).</param>
public sealed record PendingInstallationPermissions(
    string HtmlUrl,
    IReadOnlyList<PendingPermission> Pending,
    IReadOnlyList<PendingPermission>? MissingFromApp = null,
    IReadOnlyList<string>? MissingEvents = null,
    string? AppSettingsUrl = null)
{
    public IReadOnlyList<PendingPermission> MissingFromApp { get; init; } = MissingFromApp ?? [];
    public IReadOnlyList<string> MissingEvents { get; init; } = MissingEvents ?? [];
}

/// <param name="Name">The forge's permission key, e.g. <c>actions</c> or <c>pull_requests</c>.</param>
/// <param name="DisplayName">The key for people: <c>Pull requests</c>.</param>
/// <param name="Declared">The level the app asks for.</param>
/// <param name="Accepted">The level the installation granted; <c>null</c> when it granted none.</param>
public sealed record PendingPermission(string Name, string DisplayName, string Declared, string? Accepted);
