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
    private static readonly IReadOnlyDictionary<string, (string Ecosystem, IReadOnlyList<string> Globs)> Lists =
        new Dictionary<string, (string, IReadOnlyList<string>)>(StringComparer.Ordinal)
        {
            ["dotnet"] = ("nuget", [
                "Microsoft.AspNetCore.*",
                "Microsoft.Extensions.*",
                "Microsoft.EntityFrameworkCore*",
                "Microsoft.NET.*",
                "System.*",
            ]),
            ["node"] = ("npm", ["@types/node"]),
        };

    /// <summary>The built-in companion globs of an SDK; none for an SDK without a lockstep package family.</summary>
    public static IReadOnlyList<string> BuiltIn(string sdk) => Lists.TryGetValue(sdk, out var list) ? list.Globs : [];

    /// <summary>The package ecosystem an SDK's companions live in, or null when it has none.</summary>
    public static string? Ecosystem(string sdk) => Lists.TryGetValue(sdk, out var list) ? list.Ecosystem : null;
}
