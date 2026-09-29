using System.Text.Json;

namespace MintPlayer.Spark.Authorization.Identity;

/// <summary>
/// Adds application fields to the profile page (<c>GET/POST /spark/auth/manage/profile</c>, #460 D16)
/// and validates them server-side. Register any number: <c>services.AddScoped&lt;ISparkProfileContributor&lt;AppUser&gt;, MyFields&gt;()</c>.
/// </summary>
/// <remarks>
/// <para>
/// Each contributor owns the field names in <see cref="Fields"/>; a posted field no contributor owns is
/// refused. <see cref="ValidateAsync"/> runs for every contributor first, and
/// <see cref="ApplyAsync"/> runs only when nobody reported an error, so a save is all-or-nothing.
/// </para>
/// <para>
/// ⚠️ <see cref="ApplyAsync"/> must only change the <typeparamref name="TUser"/> it is handed: the
/// endpoint saves the user once, after every contributor applied, and a refused save (a user-name
/// clash, say) writes nothing — which holds only while nothing was written elsewhere.
/// </para>
/// </remarks>
public interface ISparkProfileContributor<TUser>
    where TUser : SparkUser
{
    /// <summary>The field names this contributor reads and writes (matched case-insensitively).</summary>
    IReadOnlyCollection<string> Fields { get; }

    /// <summary>The current values, for the profile page.</summary>
    ValueTask<IReadOnlyDictionary<string, object?>> GetAsync(TUser user, CancellationToken cancellationToken);

    /// <summary>Checks the posted values of this contributor's fields (only those present in the request) and reports problems to <paramref name="errors"/>.</summary>
    ValueTask ValidateAsync(TUser user, IReadOnlyDictionary<string, JsonElement> values, SparkProfileErrors errors, CancellationToken cancellationToken);

    /// <summary>Writes the (validated) values onto <paramref name="user"/>.</summary>
    ValueTask ApplyAsync(TUser user, IReadOnlyDictionary<string, JsonElement> values, CancellationToken cancellationToken);
}

/// <summary>Field-keyed validation messages for a profile save; answered as a validation problem (400).</summary>
public sealed class SparkProfileErrors
{
    private readonly Dictionary<string, List<string>> errors = new(StringComparer.OrdinalIgnoreCase);

    public void Add(string field, string message)
    {
        if (!errors.TryGetValue(field, out var list))
            errors[field] = list = [];
        list.Add(message);
    }

    public bool HasErrors => errors.Count > 0;

    public IDictionary<string, string[]> ToDictionary()
        => errors.ToDictionary(e => e.Key, e => e.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Contributes a section to the personal-data export (<c>GET /spark/auth/manage/personal-data</c>,
/// #460 D8). Multi-registered; each result is serialized under <see cref="Name"/>.
/// </summary>
public interface ISparkPersonalDataContributor<TUser>
    where TUser : SparkUser
{
    /// <summary>The section's key in the export. Unique per application.</summary>
    string Name { get; }

    /// <summary>The user's personal data held by this part of the application; <see langword="null"/> omits the section.</summary>
    Task<object?> GetPersonalDataAsync(TUser user, CancellationToken cancellationToken);
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
public interface ISparkAccountDeletionHandler<TUser>
    where TUser : SparkUser
{
    Task OnDeletingAccountAsync(TUser user, CancellationToken cancellationToken);
}

/// <summary>
/// Runs <b>after</b> the store deleted an account (<c>DELETE /spark/auth/manage/account</c>) — the
/// place for anything that must only happen once the deletion is certain, such as a goodbye mail.
/// Multi-registered, run in registration order, and never run when a
/// <see cref="ISparkAccountDeletionHandler{TUser}"/> or the store refused the deletion.
/// </summary>
/// <remarks>
/// The account is already gone, so a handler that throws is logged and the remaining handlers still
/// run; the response stays 204 (a retry could only answer 404). <paramref name="user"/> is the
/// instance as it was loaded before the delete — read, never save it.
/// </remarks>
public interface ISparkAccountDeletedHandler<TUser>
    where TUser : SparkUser
{
    Task OnAccountDeletedAsync(TUser user, CancellationToken cancellationToken);
}
