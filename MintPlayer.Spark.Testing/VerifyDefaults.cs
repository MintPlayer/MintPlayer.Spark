using System.Runtime.CompilerServices;
using VerifyTests;

namespace MintPlayer.Spark.Testing;

/// <summary>
/// Centralizes Verify snapshot configuration for Spark tests.
/// Snapshots land under <c>VerifyResults/{TestClassName}/{TestMethodName}.verified.*</c>.
/// Mirrors the path convention used by CronosCore.RavenDB.UnitTests.
/// </summary>
public static class VerifyDefaults
{
    public static void Initialize()
    {
        Verifier.DerivePathInfo(
            (sourceFile, projectDirectory, type, method) => new PathInfo(
                directory: Path.Combine(projectDirectory, "VerifyResults", type.Name),
                typeName: type.Name,
                methodName: method.Name));
    }
}

internal static class ModuleInit
{
    [ModuleInitializer]
    public static void Run() => VerifyDefaults.Initialize();
}
