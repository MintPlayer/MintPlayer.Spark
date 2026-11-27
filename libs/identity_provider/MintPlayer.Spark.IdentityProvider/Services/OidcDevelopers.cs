using System.Security.Cryptography;
using System.Text;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.IdentityProvider.Configuration;
using MintPlayer.Spark.IdentityProvider.Models;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// Reads and writes a user's developer status (<see cref="OidcDeveloper"/>), a document of its own per user (#536 D47).
/// <para>
/// <b>Read</b> by a load in a session of its own, which is strongly consistent, as a membership decision needs.
/// <b>Written</b> into the caller's session, so the status change commits together with its audit event.
/// </para>
/// </summary>
public sealed class OidcDevelopers(ISparkStore store, SparkIdentityProviderOptions options)
{
    private const string CollectionPrefix = "oidcdevelopers/";

    /// <summary>
    /// The id of <paramref name="userId"/>'s status: a hash, so any user id folds into one well-formed id. Ids compare
    /// ignoring case, so the user id is hashed in lower case.
    /// </summary>
    public static string DocumentId(string userId)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);
        return CollectionPrefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(userId.ToLowerInvariant())));
    }

    /// <summary>The user's developer status, or null when they never asked.</summary>
    public async Task<OidcDeveloper?> GetAsync(string userId, CancellationToken ct = default)
    {
        using var session = store.OpenSession();
        return await session.LoadAsync<OidcDeveloper>(DocumentId(userId), ct);
    }

    /// <summary>
    /// Whether the user is an approved developer under the terms in force. Accepting an older version
    /// of the terms is not enough: raising <c>TermsVersion</c> suspends the portal until they accept again.
    /// </summary>
    public bool IsActive(OidcDeveloper? developer)
        => developer is { Status: OidcDeveloperStatuses.Approved } && developer.TermsVersion >= options.Developers.TermsVersion;

    /// <summary>
    /// Writes <paramref name="developer"/> as <paramref name="userId"/>'s status into <paramref name="session"/>; the caller's
    /// SaveChanges commits it. <paramref name="developer"/> may come from <see cref="GetAsync"/> (another session): its
    /// values are copied onto the document this session tracks.
    /// </summary>
    public static async Task SetAsync(ISparkSession session, string userId, OidcDeveloper developer, CancellationToken ct = default)
    {
        var id = DocumentId(userId);
        var stored = await session.LoadAsync<OidcDeveloper>(id, ct);
        if (stored is null)
        {
            stored = new OidcDeveloper { Id = Id<OidcDeveloper>.FromTrusted(id) };
            await session.StoreAsync(stored, id, ct);
        }
        stored.UserId = userId;
        stored.Status = developer.Status;
        stored.TermsVersion = developer.TermsVersion;
        stored.RequestedAt = developer.RequestedAt;
        stored.DecidedAt = developer.DecidedAt;
        stored.DecidedBy = developer.DecidedBy;
        stored.Note = developer.Note;
    }
}
