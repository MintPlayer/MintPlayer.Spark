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

/// <summary><c>Spark:IdentityProvider:Audit</c> (<c>docs/identity_provider_platform_PRD.md</c> D9).</summary>
public class SparkIdentityProviderAuditOptions
{
    /// <summary>How long an audit event is kept before RavenDB deletes it.</summary>
    public int RetentionDays { get; set; } = 180;
}
