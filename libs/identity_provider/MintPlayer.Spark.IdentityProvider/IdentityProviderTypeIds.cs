namespace MintPlayer.Spark.IdentityProvider;

/// <summary>
/// The ids of this library's own JSON-only types, as its <c>App_Data/Model</c> files ship them: UUIDv5 of
/// <c>identity-provider:{DeclaredName}</c> (composition D5). Its code looks them up by id, never by a name literal, so
/// an application that renames one (<c>[assembly: PersistentObjectName]</c>, D28) changes nothing here.
/// </summary>
internal static class IdentityProviderTypeIds
{
    /// <summary><c>Model/OidcInvitation.json</c>: the team invitation prompt, never stored.</summary>
    public static readonly Guid OidcInvitation = Guid.Parse("069707aa-93e9-5ebd-a5ed-df73eadfe9b4");

    /// <summary><c>Model/OidcApplication.json</c> (a stored type, listed for its page route): where an approval request about an application links to.</summary>
    public static readonly Guid OidcApplication = Guid.Parse("d0d0db0e-d372-52af-8bab-7c6b7e249347");
}
