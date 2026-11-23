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
