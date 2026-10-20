namespace MintPlayer.Spark.Authorization;

/// <summary>
/// The ids of this library's own JSON-only types, as its <c>App_Data/Model</c> files ship them: UUIDv5 of
/// <c>authorization:{DeclaredName}</c> (composition D5). Its code looks them up by id, never by a name literal, so an
/// application that renames one (<c>[assembly: PersistentObjectName("authorization", "Passkeys", "…")]</c>, D28)
/// changes nothing here.
/// </summary>
internal static class AuthorizationTypeIds
{
    /// <summary><c>Model/Passkeys.json</c>: the signed-in user's passkeys page.</summary>
    public static readonly Guid Passkeys = Guid.Parse("cd8a0767-2a18-5c73-a608-14cf9efe99ac");

    /// <summary><c>Model/PasskeyRename.json</c>: the rename prompt.</summary>
    public static readonly Guid PasskeyRename = Guid.Parse("da8a90fd-91ae-593a-8b48-0ad102082412");
}
