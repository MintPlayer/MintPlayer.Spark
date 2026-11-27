using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// The application's users, as the identity provider reads them: by id, as the user type <c>AddAuthentication&lt;TUser&gt;()</c>
/// registered (read lazily, because that call may come after <c>AddIdentityProvider</c>), and by email through the user
/// store's email reservation (#536 D47). No query: on RavenDB a query on the users' collection builds an auto index (D42),
/// and the reservation is consistent where an index is not.
/// </summary>
internal sealed class OidcUserDocuments(SparkModuleRegistry registry)
{
    /// <summary>The application's user type.</summary>
    public Type UserType => registry.IdentityUserType ?? throw new InvalidOperationException(
        "The identity provider needs the application's user type. Call AddAuthentication<TUser>() on the Spark builder.");

    /// <summary>The users of <paramref name="ids"/> that exist, by id.</summary>
    public async Task<IReadOnlyDictionary<string, SparkUser>> LoadAsync(ISparkSession session, IEnumerable<string> ids, CancellationToken ct)
    {
        var loaded = await session.LoadAsync(UserType, ids.Distinct(StringComparer.OrdinalIgnoreCase), ct);
        return loaded
            .Where(pair => pair.Value is SparkUser)
            .ToDictionary(pair => pair.Key, pair => (SparkUser)pair.Value!, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The user holding <paramref name="normalizedEmail"/>, or null. The user store reserves every user's address when it
    /// writes the user (<see cref="SparkUserReservations"/>); a user written around it is not found.
    /// </summary>
    public async Task<SparkUser?> FindByEmailAsync(ISparkSession session, string normalizedEmail, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(normalizedEmail))
            return null;
        var owner = await session.GetReservationAsync(SparkUserReservations.EmailKey(normalizedEmail), ct);
        if (owner is null)
            return null;
        // Reservation keys compare ignoring case; the address must match exactly as normalized.
        return await session.LoadAsync(UserType, owner, ct) is SparkUser user
            && string.Equals(user.NormalizedEmail ?? user.Email?.ToUpperInvariant(), normalizedEmail, StringComparison.Ordinal)
            ? user
            : null;
    }
}
