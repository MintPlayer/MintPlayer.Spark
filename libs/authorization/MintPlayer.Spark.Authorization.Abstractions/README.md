# MintPlayer.Spark.Authorization.Abstractions

The user and role **document model** of `MintPlayer.Spark.Authorization`: `SparkUser`, `SparkUserClaim`,
`SparkUserLogin`, `SparkUserPasskey`, `SparkUserToken`, `SparkRole` and `SparkRoleClaim`.

It is a plain `Microsoft.NET.Sdk` package with no dependencies, so an entity library can point a
`[Reference(typeof(SparkUser))]` at a user **without** pulling in the ASP.NET Core shared framework
(#388). Everything that does need ASP.NET Core (the user and role stores, `UserManager` /
`SignInManager`, the `/spark/auth/*` endpoints, external logins) stays in
`MintPlayer.Spark.Authorization`, which references this package. Applications reference that one.

## The namespace is deliberately not the package name

The types keep the namespace `MintPlayer.Spark.Authorization.Identity`, where they lived before the
split, so no consuming code changes.

## Stored documents

RavenDB records a document's CLR type as `Namespace.Type, Assembly` in `@metadata.Raven-Clr-Type`.
Users and roles stored before this package existed name the old assembly. `MintPlayer.Spark.Authorization`
ships the migration `M_202610041800_UserDocumentsMovedAssembly`, which rewrites that value; an
application's generated `spark.AddMigrations()` registers it automatically.
