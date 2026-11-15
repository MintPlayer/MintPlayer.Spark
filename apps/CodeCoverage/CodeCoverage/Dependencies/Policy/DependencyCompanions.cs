namespace CodeCoverage.Dependencies.Policy;

/// <summary>
/// #606 D2: the packages that move with an SDK's major. They are released in lockstep with the platform, so they never move
/// on their own; they ride in the SDK's platform group.
/// </summary>
/// <remarks>
/// <para>A repository overrides the list with <c>platforms.&lt;sdk&gt;.companions</c>. An empty list turns companions off.</para>
/// <para>S2 (2026-11-09, restore of 11.0.0-rc.3.26522.148 into a <c>net10.0</c> project, SDK 11.0.100-rc.3) found two
/// kinds. <c>Microsoft.AspNetCore.Authentication.JwtBearer</c> and <c>Microsoft.EntityFrameworkCore</c> fail with NU1202
/// ("supports: net11.0"): their next major needs the next SDK. <c>Microsoft.Extensions.Hosting</c> and
/// <c>System.Text.Json</c> restore and build: they ship <c>lib/net10.0</c>, <c>net462</c> and <c>netstandard2.0</c>. They
/// stay companions for the lockstep, not for the target: ASP.NET Core 11 depends on Extensions 11, and a <c>net10.0</c>
/// library taking Extensions 11 alone would push it onto every consumer before the platform moves.</para>
/// </remarks>
public static class DependencyCompanions
{
    private static readonly IReadOnlyDictionary<string, (string Ecosystem, IReadOnlyList<DependencyCompanionEntry> Entries)> Lists =
        new Dictionary<string, (string, IReadOnlyList<DependencyCompanionEntry>)>(StringComparer.Ordinal)
        {
            ["dotnet"] = ("nuget", [
                new("Microsoft.AspNetCore.*"),
                new("Microsoft.Extensions.*"),
                new("Microsoft.EntityFrameworkCore*"),
                new("Microsoft.NET.*"),
                new("System.*"),
                // follower-cooldown-PRD H1: EF Core's tool, released with it (Dotnet.Tools #215: dotnet-ef 8.0.3 in a net11.0
                // web app's tool manifest).
                new("dotnet-ef"),
                // companion-version-maps-PRD §4.2: the NativeAOT compiler ships per runtime, on the channel's major.
                new("Microsoft.DotNet.ILCompiler"),
                // §4.2 / S1: MSBuild, Roslyn and NuGet carry their own majors; each SDK band ships one minor of each (VMR tag
                // v11.0.100: MSBuild 18.12.2, Roslyn 5.12.0, NuGet 8.0.0). A package above the SDK's own needs a newer SDK.
                new("Microsoft.Build*", Bundled: EDependencyBundledComponent.Msbuild),
                new("Microsoft.CodeAnalysis*", Bundled: EDependencyBundledComponent.Roslyn),
                new("Microsoft.Net.Compilers.Toolset", Bundled: EDependencyBundledComponent.Roslyn),
                new("NuGet.*", Bundled: EDependencyBundledComponent.Nuget),
            ]),
            ["node"] = ("npm", [new("@types/node")]),
        };

    /// <summary>The built-in companion entries of an SDK; none for an SDK without a lockstep package family.</summary>
    public static IReadOnlyList<DependencyCompanionEntry> BuiltIn(string sdk) => Lists.TryGetValue(sdk, out var list) ? list.Entries : [];

    /// <summary>The package ecosystem an SDK's companions live in, or null when it has none.</summary>
    public static string? Ecosystem(string sdk) => Lists.TryGetValue(sdk, out var list) ? list.Ecosystem : null;
}
