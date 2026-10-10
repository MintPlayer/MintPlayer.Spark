using MintPlayer.Spark.Abstractions;

namespace SeoTester.Entities;

/// <summary>
/// The abuse log (<c>docs/seo_analyzer_PRD.md</c> D28, D37): who audited or verified what, when, from which
/// organization, and what was refused. Administrators only. Expires after <c>SeoTester:AuditLog:Retention</c> (1 year),
/// set when written. Written by server code only.
/// </summary>
public class AuditLogEntry : IExpiring
{
    public string? Id { get; set; }

    public DateTime AtUtc { get; set; }

    /// <summary>What happened: see <see cref="AuditLogKinds"/>.</summary>
    public string Kind { get; set; } = string.Empty;

    public string? UserId { get; set; }
    public string? OrganizationId { get; set; }

    [Reference(typeof(Site))]
    public string? SiteId { get; set; }

    public string? Origin { get; set; }

    [Reference(typeof(AuditRun))]
    public string? RunId { get; set; }

    /// <summary>The site was verified at the time.</summary>
    public bool Verified { get; set; }

    /// <summary>A short reason for a refusal or a claim.</summary>
    public string? Detail { get; set; }

    public DateTime? ExpiresAtUtc { get; set; }
}

/// <summary>The values of <see cref="AuditLogEntry.Kind"/>.</summary>
public static class AuditLogKinds
{
    public const string AuditStarted = nameof(AuditStarted);
    public const string AuditJoined = nameof(AuditJoined);
    public const string QuotaRefused = nameof(QuotaRefused);
    public const string CoolDownRefused = nameof(CoolDownRefused);
    public const string GateRefused = nameof(GateRefused);
    public const string SiteVerified = nameof(SiteVerified);
    public const string SiteClaimRequested = nameof(SiteClaimRequested);
    public const string VerificationWithdrawn = nameof(VerificationWithdrawn);
}

/// <summary>
/// The per-target cool-down for unverified targets across all users (D17, D37). Not a persistent object: read and
/// written by <c>StartAudit</c> by id, <c>TargetCooldowns/{host}</c>. It expires with the cool-down (X1), so an expired
/// one that RavenDB has not swept yet must be treated as absent: compare <see cref="ExpiresAtUtc"/> yourself.
/// </summary>
public class TargetCooldown : IExpiring
{
    public string? Id { get; set; }
    public string Host { get; set; } = string.Empty;
    public string? RunId { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }

    public static string IdFor(string host) => "TargetCooldowns/" + host;
}
