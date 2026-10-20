namespace MintPlayer.Spark.Organizations;

/// <summary>
/// The ids of this library's own JSON-only types, as its <c>App_Data/Model</c> files ship them: UUIDv5 of
/// <c>organizations:{DeclaredName}</c> (composition D5). Its code looks them up by id, never by a name literal, so an
/// application that renames one (<c>[assembly: PersistentObjectName]</c>, D28) changes nothing here.
/// </summary>
internal static class OrganizationTypeIds
{
    /// <summary><c>Model/OrganizationInvite.json</c>: the invitation prompt, never stored.</summary>
    public static readonly Guid OrganizationInvite = Guid.Parse("ea792651-739e-5236-bf2e-15c7c9a9f341");
}
