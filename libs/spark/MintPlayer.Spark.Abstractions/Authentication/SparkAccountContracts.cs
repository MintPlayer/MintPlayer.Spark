namespace MintPlayer.Spark.Abstractions.Authentication;

/// <summary>
/// An account as the account lifecycle hooks and the <see cref="ISparkAccountDirectory"/> see it
/// (<c>docs/notification_bell_PRD.md</c> G4). Implemented by the package that owns accounts (Authorization,
/// over its stored user document), so a feature can react to sign-ins, deletions and exports without
/// referencing that package.
/// </summary>
public interface ISparkAccount
{
    /// <summary>The account's id, the one <see cref="ISparkCurrentPrincipal.Id"/> reports.</summary>
    string Id { get; }

    /// <summary>The user name, or <see langword="null"/>.</summary>
    string? UserName { get; }

    /// <summary>The email address, or <see langword="null"/>.</summary>
    string? Email { get; }

    /// <summary>Whether <see cref="Email"/> was confirmed. An unconfirmed address proves nothing: never mail it.</summary>
    bool EmailConfirmed { get; }

    /// <summary>The culture the user chose for mails (<c>nl</c>, <c>en-GB</c>), or <see langword="null"/>.</summary>
    string? PreferredCulture { get; }
}

/// <summary>
/// Resolves account ids to <see cref="ISparkAccount"/>s, batched (G4.2): a feature that mails or pushes to
/// users (Notifications) asks this instead of loading the account package's user document. Optional: a
/// feature without one treats every id as unknown, and an app without accounts registers none.
/// </summary>
public interface ISparkAccountDirectory
{
    /// <summary>Every known id among <paramref name="ids"/>; an unknown id is missing from the result.</summary>
    Task<IReadOnlyDictionary<string, ISparkAccount>> FindAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs after a user <b>signed in</b>: every fresh sign-in (password, second factor, recovery code,
/// external login, passkey), never a refresh of an existing session. The place for work an account needs
/// before it is used, such as the Organizations package's personal organization
/// (<c>docs/seo_analyzer_PRD.md</c> D21), or claiming what was addressed to the visitor the browser was
/// before it signed in (<c>docs/notification_bell_PRD.md</c> R7). Multi-registered, run in registration order.
/// </summary>
/// <remarks>
/// <para>
/// Called before the sign-in ticket is issued, inside the sign-in request: the request's cookies (the
/// visitor's included) are still readable, and the response has not started. A handler that throws is
/// logged and the sign-in still succeeds: a sign-in is never refused for work that can be retried, so a
/// handler must be idempotent (it runs again on the next sign-in).
/// </para>
/// <para>
/// Runs on every sign-in, so keep it cheap: a load by id, not a query.
/// </para>
/// </remarks>
public interface ISparkSignedInHandler
{
    Task OnSignedInAsync(ISparkAccount account, CancellationToken cancellationToken);
}

/// <summary>
/// Runs before an account is deleted (<c>DELETE /spark/auth/manage/account</c>, #460 D8) — erase or
/// anonymise what the application holds about the user. Multi-registered, run in registration order.
/// </summary>
/// <remarks>
/// <para>
/// The account itself is deleted <b>last</b>, by the store, which also releases its email and passkey
/// reservations. A handler that throws stops the deletion with the account intact, so the request can
/// be retried — handlers must therefore be idempotent (a retry runs every handler again).
/// </para>
/// <para>
/// Audit fields hold user ids, resolved to a name at read time, so they need no rewrite; RavenDB
/// revisions are not rewritten either (documented in the authorization guide). Content-level personal
/// data is the application's to handle here.
/// </para>
/// </remarks>
public interface ISparkAccountDeletionHandler
{
    Task OnDeletingAccountAsync(ISparkAccount account, CancellationToken cancellationToken);
}

/// <summary>
/// Runs <b>after</b> the store deleted an account (<c>DELETE /spark/auth/manage/account</c>) — the
/// place for anything that must only happen once the deletion is certain, such as a goodbye mail.
/// Multi-registered, run in registration order, and never run when a
/// <see cref="ISparkAccountDeletionHandler"/> or the store refused the deletion.
/// </summary>
/// <remarks>
/// The account is already gone, so a handler that throws is logged and the remaining handlers still
/// run; the response stays 204 (a retry could only answer 404). <paramref name="account"/> is the
/// account as it was loaded before the delete.
/// </remarks>
public interface ISparkAccountDeletedHandler
{
    Task OnAccountDeletedAsync(ISparkAccount account, CancellationToken cancellationToken);
}

/// <summary>
/// Contributes a section to the personal-data export (<c>GET /spark/auth/manage/personal-data</c>,
/// #460 D8). Multi-registered; each result is serialized under <see cref="Name"/>.
/// </summary>
public interface ISparkPersonalDataContributor
{
    /// <summary>The section's key in the export. Unique per application.</summary>
    string Name { get; }

    /// <summary>The user's personal data held by this part of the application; <see langword="null"/> omits the section.</summary>
    Task<object?> GetPersonalDataAsync(ISparkAccount account, CancellationToken cancellationToken);
}
