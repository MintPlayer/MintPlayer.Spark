using System.Security.Cryptography;
using System.Text;
using MintPlayer.Spark.Abstractions.Storage;

namespace MintPlayer.Spark.Authorization.Identity;

/// <summary>
/// The store reservations (#536 S6; compare exchange on RavenDB) through which <see cref="UserStore{TUser}"/> finds a
/// user by email, by user name and by external login, and which make each of them unique.
/// </summary>
/// <remarks>
/// ⚠️ <b>A user written around the user store is not found by name, email or login.</b> The store claims these keys
/// when it creates or updates a user; a user document written any other way (a raw session in a seeder or a test, an
/// import, a patch that renames) holds no reservation, so <c>FindByNameAsync</c>, <c>FindByEmailAsync</c> and
/// <c>FindByLoginAsync</c> miss it and sign-in creates a second account or refuses one. Such code calls
/// <see cref="ClaimAsync"/> after writing the user, or writes the <see cref="KeysOf"/> itself (an importer that writes
/// compare exchange values directly: the value is the user's id). Users that existed when the name and login keys were
/// introduced are claimed once by the migration <c>M_202611251200_UserLookupReservations</c>.
/// </remarks>
public static class SparkUserReservations
{
    public const string EmailPrefix = "emails/";
    public const string UserNamePrefix = "usernames/";
    public const string LoginPrefix = "logins/";

    /// <summary>The key of an email: <c>emails/{normalized email, lower case}</c>, the compare exchange key RavenDB held before #536.</summary>
    public static string EmailKey(string normalizedEmail) => EmailPrefix + normalizedEmail.ToLowerInvariant();

    /// <summary>The key of a user name: <c>usernames/{normalized name, lower case}</c>.</summary>
    public static string UserNameKey(string normalizedUserName) => UserNamePrefix + normalizedUserName.ToLowerInvariant();

    /// <summary>
    /// The key of an external login. A provider key may be case-sensitive and of any length, while reservation keys
    /// compare case-insensitively, so the key is a lower-case hex SHA-256 over the provider and the key.
    /// </summary>
    public static string LoginKey(string loginProvider, string providerKey)
        => LoginPrefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(loginProvider + "\n" + providerKey)));

    /// <summary>The keys <paramref name="user"/> must hold: its email, its user name and each of its logins (passkeys are claimed on enrollment).</summary>
    public static HashSet<string> KeysOf(SparkUser user)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if ((user.NormalizedEmail ?? user.Email?.ToUpperInvariant()) is { Length: > 0 } email)
            keys.Add(EmailKey(email));
        if (user.NormalizedUserName is { Length: > 0 } name)
            keys.Add(UserNameKey(name));
        foreach (var login in user.Logins)
            keys.Add(LoginKey(login.LoginProvider, login.ProviderKey));
        return keys;
    }

    /// <summary>
    /// Claims <paramref name="user"/>'s keys for <paramref name="userId"/>, for a user written around the user store.
    /// Claiming a key the user already holds changes nothing. A key another existing user holds stays with that user;
    /// the keys this user could not claim are returned.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ClaimAsync(ISparkStore store, string userId, SparkUser user, CancellationToken cancellationToken = default)
    {
        var keys = KeysOf(user);
        if (keys.Count == 0 || await TryReserveAsync(store, userId, keys, cancellationToken))
            return [];

        // One of them is someone else's: claim the others one by one.
        var taken = new List<string>();
        foreach (var key in keys)
        {
            if (!await TryReserveAsync(store, userId, [key], cancellationToken))
                taken.Add(key);
        }
        return taken;
    }

    private static async Task<bool> TryReserveAsync(ISparkStore store, string userId, IEnumerable<string> keys, CancellationToken cancellationToken)
    {
        using var session = store.OpenSession();
        foreach (var key in keys)
            session.Reserve(key, userId);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (SparkReservationTakenException)
        {
            return false;
        }
    }
}
