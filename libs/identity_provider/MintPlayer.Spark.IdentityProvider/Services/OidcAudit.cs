using MintPlayer.Spark.IdentityProvider.Configuration;
using MintPlayer.Spark.IdentityProvider.Models;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// Writes <see cref="OidcAuditEvent"/>s (<c>docs/identity_provider_platform_PRD.md</c> D9) into the
/// caller's session, so an event is committed exactly when the change it records is: a refused save
/// leaves no event behind, and a saved change never lacks one.
/// </summary>
public sealed class OidcAudit(SparkIdentityProviderOptions options)
{
    /// <summary>Adds an event to <paramref name="session"/>. The caller's SaveChanges commits it.</summary>
    public async Task RecordAsync(
        IAsyncDocumentSession session,
        string kind,
        string? actorId,
        string? applicationId = null,
        string? subjectId = null,
        string? ipAddress = null,
        IReadOnlyDictionary<string, string>? details = null,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var audit = new OidcAuditEvent
        {
            At = now,
            Kind = kind,
            ActorId = actorId,
            ApplicationId = applicationId,
            SubjectId = subjectId,
            IpAddress = ipAddress,
            Details = details is null ? [] : new Dictionary<string, string>(details),
        };
        await session.StoreAsync(audit, "OidcAuditEvents/", ct);
        OidcExpiry.Stamp(session, audit, now.AddDays(Math.Max(1, options.Audit.RetentionDays)));
    }
}
