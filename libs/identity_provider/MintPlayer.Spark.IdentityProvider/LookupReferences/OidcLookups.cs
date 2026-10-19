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
        new OidcMemberRole { Key = OidcMemberRoles.Admin, Description = "Everything: members, secrets, delete, mode", Values = _TS(("en", "Admin"), ("fr", "Administrateur"), ("nl", "Beheerder")) },
        new OidcMemberRole { Key = OidcMemberRoles.Developer, Description = "Settings, URIs and scopes", Values = _TS(("en", "Developer"), ("fr", "Développeur"), ("nl", "Ontwikkelaar")) },
        new OidcMemberRole { Key = OidcMemberRoles.Tester, Description = "May sign in while the application is in development", Values = _TS(("en", "Tester"), ("fr", "Testeur"), ("nl", "Tester")) },
    ];
}

/// <summary>An application's mode (D4).</summary>
public sealed class OidcApplicationMode : TransientLookupReference<string>
{
    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;
    private OidcApplicationMode() { }

    public static IReadOnlyCollection<OidcApplicationMode> Items { get; } =
    [
        new OidcApplicationMode { Key = OidcApplicationModes.Development, Description = "Only the team can sign in", Values = _TS(("en", "Development"), ("fr", "Développement"), ("nl", "Ontwikkeling")) },
        new OidcApplicationMode { Key = OidcApplicationModes.Live, Description = "Anyone can sign in", Values = _TS(("en", "Live"), ("fr", "En ligne"), ("nl", "Live")) },
    ];
}

/// <summary>What a resource is (D1).</summary>
public sealed class OidcResourceKind : TransientLookupReference<string>
{
    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;
    private OidcResourceKind() { }

    public static IReadOnlyCollection<OidcResourceKind> Items { get; } =
    [
        new OidcResourceKind { Key = OidcResourceKinds.Identity, Description = "A scope of user claims", Values = _TS(("en", "Identity"), ("fr", "Identité"), ("nl", "Identiteit")) },
        new OidcResourceKind { Key = OidcResourceKinds.Api, Description = "A protected API, named by its audience", Values = _TS(("en", "API"), ("fr", "API"), ("nl", "API")) },
    ];
}

/// <summary>Whether a client can keep a secret.</summary>
public sealed class OidcClientType : TransientLookupReference<string>
{
    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;
    private OidcClientType() { }

    public static IReadOnlyCollection<OidcClientType> Items { get; } =
    [
        new OidcClientType { Key = "confidential", Description = "A server that can keep a secret", Values = _TS(("en", "Confidential"), ("fr", "Confidentiel"), ("nl", "Vertrouwelijk")) },
        new OidcClientType { Key = "public", Description = "A browser or mobile app that cannot keep a secret", Values = _TS(("en", "Public"), ("fr", "Public"), ("nl", "Publiek")) },
    ];
}

/// <summary>How consent is asked.</summary>
public sealed class OidcConsentType : TransientLookupReference<string>
{
    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;
    private OidcConsentType() { }

    public static IReadOnlyCollection<OidcConsentType> Items { get; } =
    [
        new OidcConsentType { Key = "explicit", Description = "The user is asked on the consent screen", Values = _TS(("en", "Ask the user"), ("fr", "Demander à l'utilisateur"), ("nl", "De gebruiker vragen")) },
        new OidcConsentType { Key = "implicit", Description = "Granted automatically: first-party applications only", Values = _TS(("en", "Automatic"), ("fr", "Automatique"), ("nl", "Automatisch")) },
    ];
}
