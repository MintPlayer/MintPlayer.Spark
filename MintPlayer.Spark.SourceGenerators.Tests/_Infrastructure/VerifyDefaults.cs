using System.Runtime.CompilerServices;
using VerifyTests;

namespace MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

/// <summary>
/// Pins Verify snapshot files to <c>VerifyResults/{TestClassName}/{TestMethodName}.verified.*</c>
/// — the same convention used by <c>MintPlayer.Spark.Testing.VerifyDefaults</c>. Duplicated here
/// (rather than referenced) because <c>MintPlayer.Spark.Testing</c> drags in RavenDB, which has
/// no business in a generator-tests project.
/// </summary>
internal static class VerifyDefaults
{
    [ModuleInitializer]
    public static void Initialize()
    {
        Verifier.DerivePathInfo(
            (sourceFile, projectDirectory, type, method) => new PathInfo(
                directory: Path.Combine(projectDirectory, "VerifyResults", type.Name),
                typeName: type.Name,
                methodName: method.Name));
    }
}
