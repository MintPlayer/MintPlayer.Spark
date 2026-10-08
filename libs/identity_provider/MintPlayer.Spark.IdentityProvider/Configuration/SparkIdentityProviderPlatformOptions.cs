namespace MintPlayer.Spark.IdentityProvider.Configuration;

/// <summary><c>Spark:IdentityProvider:Developers</c> (<c>docs/identity_provider_platform_PRD.md</c> D2).</summary>
public class SparkIdentityProviderDeveloperOptions
{
    /// <summary>
    /// Whether an identity-provider administrator must approve a request to become a developer.
    /// <c>false</c> makes a user a developer the moment they accept the terms.
    /// </summary>
    public bool RequireApproval { get; set; } = true;

    /// <summary>
    /// The version of the developer terms in force. Raising it makes every developer accept the
    /// terms again before their next portal action: until they do, they are not in the developers group.
    /// </summary>
    public int TermsVersion { get; set; } = 1;

    /// <summary>Where the developer terms are published, linked from the request page. Optional.</summary>
    public string? TermsUrl { get; set; }
}

/// <summary><c>Spark:IdentityProvider:Apps</c> (<c>docs/identity_provider_platform_PRD.md</c> D4, D5).</summary>
public class SparkIdentityProviderAppOptions
{
    /// <summary>Whether an administrator must approve an application's switch to Live.</summary>
    public bool RequireReviewToGoLive { get; set; }

    /// <summary>How many redirect URIs one application may register.</summary>
    public int MaxRedirectUris { get; set; } = 20;

    /// <summary>How long an invitation to an application's team stays valid.</summary>
    public TimeSpan InvitationLifetime { get; set; } = TimeSpan.FromDays(7);
}

/// <summary><c>Spark:IdentityProvider:TwoFactor</c> (#490 PRD D11).</summary>
public class SparkIdentityProviderTwoFactorOptions
{
    /// <summary>Whether a user with two-factor authentication must pass it at the provider's sign-in.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary><c>Spark:IdentityProvider:Branding</c>: how the <c>/connect/*</c> pages present the provider (D7).</summary>
public class SparkIdentityProviderBranding
{
    /// <summary>The provider's name, shown beside the logo and in the window title.</summary>
    public string? ProductName { get; set; }

    /// <summary>The logo's address.</summary>
    public string? LogoUrl { get; set; }

    /// <summary>CSS appended to the pages' own stylesheet, e.g. to override the <c>--idp-*</c> colours.</summary>
    public string? ExtraCss { get; set; }
}

/// <summary><c>Spark:IdentityProvider:Keys</c>: the signing-key rotation schedule (D8, I10).</summary>
public class SparkIdentityProviderKeyOptions
{
    /// <summary>How long a key signs before the next one takes over.</summary>
    public int RotationDays { get; set; } = 90;

    /// <summary>How long the next key is published before it signs, so relying parties have it cached.</summary>
    public int PrePublishDays { get; set; } = 2;

    /// <summary>How long a retired key stays published, covering the longest-lived token it signed.</summary>
    public int RetainRetiredDays { get; set; } = 14;
}

/// <summary><c>Spark:IdentityProvider:Audit</c> (<c>docs/identity_provider_platform_PRD.md</c> D9).</summary>
public class SparkIdentityProviderAuditOptions
{
    /// <summary>How long an audit event is kept before RavenDB deletes it.</summary>
    public int RetentionDays { get; set; } = 180;
}
