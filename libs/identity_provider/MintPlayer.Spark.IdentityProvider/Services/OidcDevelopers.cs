using MintPlayer.Spark.IdentityProvider.Configuration;
using MintPlayer.Spark.IdentityProvider.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Commands.Batches;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// Reads and writes a user's developer status (<see cref="OidcDeveloper"/>), which lives as the
/// <c>Developer</c> field of the user's own document (<c>docs/identity_provider_platform_PRD.md</c> D1, D2).
/// <para>
/// <b>Read</b> by loading the user document as <see cref="DeveloperHolder"/> in a session of its own:
/// the same id can be tracked as the application's user type in the request session, and one session
/// cannot hold one id as two types. A load is strongly consistent, which a membership decision needs.
/// </para>
/// <para>
/// <b>Written</b> with a patch deferred into the caller's session, so the status change commits
/// together with its audit event, and the user document's other fields are never rewritten.
/// </para>
/// </summary>
public sealed class OidcDevelopers(IDocumentStore store, SparkIdentityProviderOptions options)
{
    private sealed class DeveloperHolder
    {
        public string? Id { get; set; }
        public OidcDeveloper? Developer { get; set; }
    }

    /// <summary>The user's developer status, or null when they never asked.</summary>
    public async Task<OidcDeveloper?> GetAsync(string userId, CancellationToken ct = default)
    {
        using var session = store.OpenAsyncSession();
        var holder = await session.LoadAsync<DeveloperHolder>(userId, ct);
        return holder?.Developer;
    }

    /// <summary>
    /// Whether the user is an approved developer under the terms in force. Accepting an older version
    /// of the terms is not enough: raising <c>TermsVersion</c> suspends the portal until they accept again.
    /// </summary>
    public bool IsActive(OidcDeveloper? developer)
        => developer is { Status: OidcDeveloperStatuses.Approved } && developer.TermsVersion >= options.Developers.TermsVersion;

    /// <summary>Defers writing <paramref name="developer"/> onto the user document; the caller's SaveChanges commits it.</summary>
    public static void Set(IAsyncDocumentSession session, string userId, OidcDeveloper developer)
    {
        session.Advanced.Defer(new PatchCommandData(userId, null, new PatchRequest
        {
            Script = $"this.{OidcDeveloper.FieldName} = args.developer;",
            Values = { ["developer"] = developer },
        }));
    }
}
