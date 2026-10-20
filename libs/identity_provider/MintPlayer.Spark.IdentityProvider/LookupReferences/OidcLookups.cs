using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.IdentityProvider.LookupReferences;

// The identity provider's choice lists. Keys are stored strings; each lookup class is generated from its enum, and the
// labels are the translations.json keys lookup.{Enum}.{Member} (commerce X20 item 5).

/// <summary>An application member's role (<c>docs/identity_provider_platform_PRD.md</c> D3). Keys: <c>OidcMemberRoles</c>.</summary>
[SparkLookup(StringKeys = true)]
public enum EOidcMemberRole
{
    /// <summary>Everything: members, secrets, delete, mode.</summary>
    Admin,
    /// <summary>Settings, URIs and scopes.</summary>
    Developer,
    /// <summary>May sign in while the application is in development.</summary>
    Tester,
}

/// <summary>An application's mode (D4). Keys: <c>OidcApplicationModes</c>.</summary>
[SparkLookup(StringKeys = true)]
public enum EOidcApplicationMode
{
    /// <summary>Only the team can sign in.</summary>
    Development,
    /// <summary>Anyone can sign in.</summary>
    Live,
}

/// <summary>What a resource is (D1). Keys: <c>OidcResourceKinds</c>.</summary>
[SparkLookup(StringKeys = true)]
public enum EOidcResourceKind
{
    /// <summary>A scope of user claims.</summary>
    Identity,
    /// <summary>A protected API, named by its audience.</summary>
    Api,
}

/// <summary>Whether a client can keep a secret.</summary>
[SparkLookup(StringKeys = true)]
public enum EOidcClientType
{
    /// <summary>A server that can keep a secret.</summary>
    [SparkLookupKey("confidential")] Confidential,
    /// <summary>A browser or mobile app that cannot keep a secret.</summary>
    [SparkLookupKey("public")] Public,
}

/// <summary>How consent is asked.</summary>
[SparkLookup(StringKeys = true)]
public enum EOidcConsentType
{
    /// <summary>The user is asked on the consent screen.</summary>
    [SparkLookupKey("explicit")] Explicit,
    /// <summary>Granted automatically: first-party applications only.</summary>
    [SparkLookupKey("implicit")] Implicit,
}
