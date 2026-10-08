using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.IdentityProvider.Models;

namespace MintPlayer.Spark.IdentityProvider.LookupReferences;

/// <summary>An application member's role (<c>docs/identity_provider_platform_PRD.md</c> D3).</summary>
public sealed class OidcMemberRole : TransientLookupReference<string>
{
    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;
    private OidcMemberRole() { }

    public static IReadOnlyCollection<OidcMemberRole> Items { get; } =
    [
        new OidcMemberRole { Key = OidcMemberRoles.Admin, Description = "Everything: members, secrets, delete, mode", Values = _TS("Admin", "Administrateur", "Beheerder") },
        new OidcMemberRole { Key = OidcMemberRoles.Developer, Description = "Settings, URIs and scopes", Values = _TS("Developer", "Développeur", "Ontwikkelaar") },
        new OidcMemberRole { Key = OidcMemberRoles.Tester, Description = "May sign in while the application is in development", Values = _TS("Tester", "Testeur", "Tester") },
    ];
}

/// <summary>An application's mode (D4).</summary>
public sealed class OidcApplicationMode : TransientLookupReference<string>
{
    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;
    private OidcApplicationMode() { }

    public static IReadOnlyCollection<OidcApplicationMode> Items { get; } =
    [
        new OidcApplicationMode { Key = OidcApplicationModes.Development, Description = "Only the team can sign in", Values = _TS("Development", "Développement", "Ontwikkeling") },
        new OidcApplicationMode { Key = OidcApplicationModes.Live, Description = "Anyone can sign in", Values = _TS("Live", "En ligne", "Live") },
    ];
}

/// <summary>What a resource is (D1).</summary>
public sealed class OidcResourceKind : TransientLookupReference<string>
{
    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;
    private OidcResourceKind() { }

    public static IReadOnlyCollection<OidcResourceKind> Items { get; } =
    [
        new OidcResourceKind { Key = OidcResourceKinds.Identity, Description = "A scope of user claims", Values = _TS("Identity", "Identité", "Identiteit") },
        new OidcResourceKind { Key = OidcResourceKinds.Api, Description = "A protected API, named by its audience", Values = _TS("API", "API", "API") },
    ];
}

/// <summary>Whether a client can keep a secret.</summary>
public sealed class OidcClientType : TransientLookupReference<string>
{
    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;
    private OidcClientType() { }

    public static IReadOnlyCollection<OidcClientType> Items { get; } =
    [
        new OidcClientType { Key = "confidential", Description = "A server that can keep a secret", Values = _TS("Confidential", "Confidentiel", "Vertrouwelijk") },
        new OidcClientType { Key = "public", Description = "A browser or mobile app that cannot keep a secret", Values = _TS("Public", "Public", "Publiek") },
    ];
}

/// <summary>How consent is asked.</summary>
public sealed class OidcConsentType : TransientLookupReference<string>
{
    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;
    private OidcConsentType() { }

    public static IReadOnlyCollection<OidcConsentType> Items { get; } =
    [
        new OidcConsentType { Key = "explicit", Description = "The user is asked on the consent screen", Values = _TS("Ask the user", "Demander à l'utilisateur", "De gebruiker vragen") },
        new OidcConsentType { Key = "implicit", Description = "Granted automatically: first-party applications only", Values = _TS("Automatic", "Automatique", "Automatisch") },
    ];
}
