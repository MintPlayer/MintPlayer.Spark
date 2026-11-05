using CodeCoverage.Versioning.Graph;
using CodeCoverage.Versioning.Releases;

namespace CodeCoverage.Versioning.Tests.Releases;

/// <summary>#570 M8b, PRD D5: the ecosystems a change touches, by the versioning engine's changed-inputs test.</summary>
public class ChangedEcosystemsTests
{
    private sealed class FakeDiff(params string[] changed) : IInputDiff
    {
        public Task<IReadOnlyList<string>> ChangedAsync(string from, string to, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(changed.Where(f => paths.Any(p => RepoPath.IsUnder(f, p))).ToList());
    }

    private static string Library(string tfm)
        => $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>{tfm}</TargetFramework></PropertyGroup></Project>";

    private static InMemoryTree Mixed() => new InMemoryTree()
        .Add("src/Core/Core.csproj", Library("net10.0"))
        .Add("src/Core/Guard.cs", "")
        .Add("src/Core.Tests/Core.Tests.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><IsPackable>false</IsPackable></PropertyGroup>"
            + "<ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.0.0\" /><ProjectReference Include=\"../Core/Core.csproj\" /></ItemGroup></Project>")
        .Add("src/Core.Tests/GuardTests.cs", "")
        .Add("package.json", """{ "name": "root", "private": true, "workspaces": ["packages/*"] }""")
        .Add("libs/ng-acme/project.json", "{}")
        .Add("libs/ng-acme/ng-package.json", """{ "dest": "../../dist/libs/ng-acme" }""")
        .Add("libs/ng-acme/package.json", """{ "name": "@acme/ng-acme", "peerDependencies": { "@angular/core": ">=21.0.0" } }""")
        .Add("libs/ng-acme/index.ts", "")
        .Add("libs/ng-acme/README.md", "")
        .Add("docs/guide.md", "")
        .Add("package-lock.json", """{ "lockfileVersion": 3, "packages": { "node_modules/@angular/core": { "version": "21.2.0" } } }""");

    private static async Task<IReadOnlyList<string>> OfAsync(VersioningSettings? settings, params string[] changed)
        => await ChangedEcosystems.OfAsync(await ProjectGraphBuilder.BuildAsync(Mixed()), new FakeDiff(changed), "base", "head",
            settings ?? new VersioningSettings { Lines = ReleaseLinePattern.Default });

    [Fact]
    public async Task A_change_touches_the_ecosystems_of_the_packages_whose_inputs_it_changes()
    {
        (await OfAsync(null, "src/Core/Guard.cs")).Should().Equal(["dotnet"]);
        (await OfAsync(null, "libs/ng-acme/index.ts")).Should().Equal(["angular"]);
        (await OfAsync(null, "libs/ng-acme/index.ts", "src/Core/Guard.cs")).Should().Equal(["angular", "dotnet"]);
    }

    [Fact]
    public async Task Paths_outside_every_package_tests_and_ignored_files_touch_nothing()
    {
        (await OfAsync(null, "docs/guide.md")).Should().BeEmpty();
        (await OfAsync(null, "src/Core.Tests/GuardTests.cs")).Should().BeEmpty("a test project is not an input of the package it tests");
        (await OfAsync(new VersioningSettings { Lines = ReleaseLinePattern.Default, Ignore = ["**/*.md"] }, "libs/ng-acme/README.md")).Should().BeEmpty();
    }

    [Fact]
    public async Task A_line_pin_moves_a_package_to_its_ecosystem()
        => (await OfAsync(new VersioningSettings
        {
            Lines = ReleaseLinePattern.Default,
            LinePins = new Dictionary<string, string> { ["Core"] = "unity" },
        }, "src/Core/Guard.cs")).Should().Equal(["unity"]);
}
