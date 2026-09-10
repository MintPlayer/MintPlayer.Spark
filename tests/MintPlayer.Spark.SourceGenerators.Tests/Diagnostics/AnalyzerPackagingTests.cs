using System.Text.RegularExpressions;

namespace MintPlayer.Spark.SourceGenerators.Tests.Diagnostics;

/// <summary>
/// Every Roslyn component this repository builds must be packed into a NuGet package, or it exists
/// only for in-repo projects and silently does nothing for consumers.
/// </summary>
/// <remarks>
/// ⚠️ <b>This has now cost the repository three times.</b> <c>MintPlayer.Spark.LibraryGenerators</c>
/// was packed nowhere, so SPARK016 — the diagnostic guarding the generated row key — reached no
/// external consumer at all; <c>[ValueObject]</c> is silently inert without the generator reference;
/// and PRD-Server-Side-Row-Lifecycle records the same shape as defect F3. Every instance failed the
/// same way: nothing errors, the feature is simply absent.
/// <para>
/// The check is deliberately textual rather than a build-output comparison. It has to hold before
/// anything is packed, and the failure it guards is an omission in a csproj, which is exactly what
/// it reads.
/// </para>
/// </remarks>
public class AnalyzerPackagingTests
{
    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MintPlayer.Spark.slnx"))
                                   && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        }
    }

    private static readonly string AllFeaturesCsproj = Path.Combine(
        RepoRoot, "libs", "all_features", "MintPlayer.Spark.AllFeatures", "MintPlayer.Spark.AllFeatures.csproj");

    public static TheoryData<string> RoslynComponents =>
    [
        "MintPlayer.Spark.SourceGenerators",
        "MintPlayer.Spark.LibraryGenerators",
        "MintPlayer.Spark.AllFeatures.SourceGenerators",
    ];

    [Theory]
    [MemberData(nameof(RoslynComponents))]
    public void Every_roslyn_component_is_packed_into_the_AllFeatures_package(string assemblyName)
    {
        var csproj = File.ReadAllText(AllFeaturesCsproj);

        var packed = Regex.IsMatch(
            csproj,
            $@"<None\s+Include=""[^""]*{Regex.Escape(assemblyName)}\.dll""[^>]*PackagePath=""analyzers/dotnet/cs""",
            RegexOptions.Singleline);

        packed.Should().BeTrue(
            $"'{assemblyName}.dll' must be packed into analyzers/dotnet/cs of MintPlayer.Spark.AllFeatures, " +
            "or none of its generators, diagnostics or code fixes reach a NuGet consumer");
    }

    /// <summary>
    /// A packed DLL is read out of the referenced project's <c>bin</c>, which only exists if
    /// something caused that project to build. The analyzer ProjectReference is what does.
    /// </summary>
    /// <remarks>
    /// Measured: adding the pack item without the reference fails the pack outright with
    /// "Could not find a part of the path ...\bin\Release\netstandard2.0" — loud, but only at pack
    /// time on a clean tree, which on this repo means in CI on the way to publishing.
    /// </remarks>
    [Theory]
    [MemberData(nameof(RoslynComponents))]
    public void Every_packed_component_is_also_referenced_so_it_gets_built(string assemblyName)
    {
        var csproj = File.ReadAllText(AllFeaturesCsproj);

        csproj.Should().Contain(
            $@"{assemblyName}.csproj"" OutputItemType=""Analyzer""",
            $"'{assemblyName}' must be an analyzer ProjectReference of AllFeatures so its output exists to pack");
    }
}
