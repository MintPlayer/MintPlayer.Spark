namespace MintPlayer.Spark.IdentityProvider.Models;

/// <summary>
/// One entry of the audit trail: the <c>OidcAuditEvents</c> collection
/// (<c>docs/identity_provider_platform_PRD.md</c> D1, D9, Q6). Written in the same session as the
/// change it records, so an event exists exactly when its change does. Retained for
/// <c>Audit.RetentionDays</c> through <c>@expires</c>. Revisions are not the audit trail: they are
/// capped on the Community licence.
/// </summary>
public class OidcAuditEvent
{
    public string? Id { get; set; }
    /// <summary>When it happened, in UTC.</summary>
    public DateTime At { get; set; }
    /// <summary>What happened: one of <see cref="OidcAuditKinds"/>.</summary>
    public string Kind { get; set; } = string.Empty;
    /// <summary>The user who did it, or null for the provider itself (a sweep, a rotation).</summary>
    public string? ActorId { get; set; }
    /// <summary>The application it concerns, if any.</summary>
    public string? ApplicationId { get; set; }
    /// <summary>The user it concerns, if any (the subject of a consent, the invitee, the requester).</summary>
    public string? SubjectId { get; set; }
    /// <summary>The caller's IP address.</summary>
    public string? IpAddress { get; set; }
    /// <summary>Kind-specific details: the scopes, the role, the secret id, the mode. Never a secret.</summary>
    public Dictionary<string, string> Details { get; set; } = [];
}

/// <summary>The values of <see cref="OidcAuditEvent.Kind"/> (D9).</summary>
public static class OidcAuditKinds
{
    public const string ApplicationCreated = "application.created";
    public const string ApplicationChanged = "application.changed";
    public const string ApplicationModeSwitched = "application.mode";
    public const string ApplicationDisabled = "application.disabled";
    public const string ApplicationDeleted = "application.deleted";
    public const string GoLiveRequested = "application.golive.requested";
    public const string GoLiveDecided = "application.golive.decided";
    public const string ScopeApprovalDecided = "application.scope.decided";
    public const string SecretGenerated = "secret.generated";
    public const string SecretRevoked = "secret.revoked";
    public const string MemberInvited = "member.invited";
    public const string MemberAccepted = "member.accepted";
    public const string MemberRemoved = "member.removed";
    public const string DeveloperRequested = "developer.requested";
    public const string DeveloperApproved = "developer.approved";
    public const string DeveloperRejected = "developer.rejected";
    public const string ConsentGranted = "consent.granted";
    public const string ConsentNarrowed = "consent.narrowed";
    public const string ConsentWithdrawn = "consent.withdrawn";
    public const string RefreshTokenReuse = "token.refresh.reuse";
    public const string KeyRotated = "key.rotated";
    public const string DynamicRegistration = "registration.dynamic";
    public const string ResourceChanged = "resource.changed";
}
