using System.Diagnostics;
using CodeCoverage.Versioning.Git;
using CodeCoverage.Versioning.Graph;

namespace CodeCoverage.Versioning.Tests.Graph;

/// <summary>M3: the static project graph, on trees shaped like the S1 spike repositories.</summary>
public class ProjectGraphTests
{
    private static string Csproj(string body, string sdk = "Microsoft.NET.Sdk")
        => $"<Project Sdk=\"{sdk}\">\n{body}\n</Project>";

    private static string Library(string tfm = "net10.0", string extra = "")
        => Csproj($"<PropertyGroup><TargetFramework>{tfm}</TargetFramework></PropertyGroup>\n{extra}");

    private static Task<ProjectGraph> Build(InMemoryTree tree) => ProjectGraphBuilder.BuildAsync(tree);

    [Fact]
    public async Task A_project_reference_chain_becomes_edges()
    {
        var graph = await Build(new InMemoryTree()
            .Add("src/Core/Core.csproj", Library())
            .Add("src/Mapper/Mapper.csproj", Library(extra: "<ItemGroup><ProjectReference Include=\"..\\Core\\Core.csproj\" /></ItemGroup>"))
            .Add("src/Cli/Cli.csproj", Library(extra: "<ItemGroup><ProjectReference Include=\"../Mapper/Mapper.csproj\" /></ItemGroup>")));

        graph.Packages.Select(p => p.Id).Should().BeEquivalentTo(["Cli", "Core", "Mapper"]);
        graph.ById("Cli")!.ProjectReferences.Should().BeEquivalentTo(["src/Mapper/Mapper.csproj"]);
        graph.InternalDependencies(graph.ById("Mapper")!).Select(d => d.Project.Id).Should().BeEquivalentTo(["Core"]);
    }

    [Fact]
    public async Task PackageId_defaults_to_AssemblyName_then_the_file_name()
    {
        var graph = await Build(new InMemoryTree()
            .Add("a/A.csproj", Library(extra: "<PropertyGroup><AssemblyName>Acme.A</AssemblyName></PropertyGroup>"))
            .Add("b/B.csproj", Library(extra: "<PropertyGroup><PackageId>Acme.Bee</PackageId><AssemblyName>Acme.B</AssemblyName></PropertyGroup>"))
            .Add("c/C.csproj", Library()));

        graph.Packages.Select(p => p.Id).Should().BeEquivalentTo(["Acme.A", "Acme.Bee", "C"]);
    }

    [Fact]
    public async Task Test_and_web_projects_are_not_packable_unless_they_say_so()
    {
        var graph = await Build(new InMemoryTree()
            .Add("t/T.csproj", Library(extra: "<ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.0.0\" /></ItemGroup>"))
            .Add("w/W.csproj", Csproj("<PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>", "Microsoft.NET.Sdk.Web"))
            .Add("x/X.csproj", Csproj("<PropertyGroup><TargetFramework>net10.0</TargetFramework><IsPackable>true</IsPackable></PropertyGroup>", "Microsoft.NET.Sdk.Web"))
            .Add("n/N.csproj", Library(extra: "<PropertyGroup><IsPackable>false</IsPackable></PropertyGroup>")));

        graph.Packages.Select(p => p.Id).Should().BeEquivalentTo(["X"]);
    }

    [Fact]
    public async Task A_linked_compile_item_outside_the_project_is_an_input()
    {
        var graph = await Build(new InMemoryTree()
            .Add("shared/Guard.cs", "class Guard {}")
            .Add("gen/Gen.csproj", Library())
            .Add("src/Lib/Lib.csproj", Library(extra: """
                <ItemGroup>
                  <Compile Include="..\..\shared\*.cs" Link="Shared\%(Filename).cs" />
                  <None Include="..\..\gen\bin\$(Configuration)\Gen.dll" Pack="true" />
                </ItemGroup>
                """)));

        graph.ById("Lib")!.Inputs.Should().Contain("shared");
        graph.ById("Lib")!.Inputs.Should().Contain("gen");
    }

    [Fact]
    public async Task Directory_Build_props_properties_resolve_and_are_inputs()
    {
        var graph = await Build(new InMemoryTree()
            .Add("Directory.Build.props", "<Project><PropertyGroup><SparkTarget>net11.0</SparkTarget><Company>Acme</Company></PropertyGroup></Project>")
            .Add("src/Directory.Build.props", """
                <Project>
                  <Import Project="$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))" />
                  <PropertyGroup><PackageId>$(Company).$(MSBuildProjectName)</PackageId></PropertyGroup>
                </Project>
                """)
            .Add("src/Lib/Lib.csproj", Csproj("<PropertyGroup><TargetFrameworks>netstandard2.0;$(SparkTarget)</TargetFrameworks></PropertyGroup>"))
            .Add("global.json", "{}"));

        var lib = graph.ById("Acme.Lib")!;
        lib.TargetFrameworks.Should().BeEquivalentTo(["netstandard2.0", "net11.0"]);
        lib.Inputs.Should().Contain("Directory.Build.props");
        lib.Inputs.Should().Contain("src/Directory.Build.props");
        lib.Inputs.Should().Contain("global.json");
        graph.AnchorOf(lib).Major.Should().Be(11);
    }

    [Fact]
    public async Task An_unresolved_target_framework_is_taken_from_the_manifest()
    {
        var graph = await Build(new InMemoryTree()
            .Add("Lib/Lib.csproj", Csproj("<PropertyGroup><TargetFramework>$(FromCommandLine)</TargetFramework></PropertyGroup>")));

        var lib = graph.ById("Lib")!;
        lib.TargetFrameworks.Should().BeEmpty();
        graph.AnchorOf(lib, ["net10.0"]).Major.Should().Be(10);
        ((Action)(() => graph.AnchorOf(lib))).Should().Throw<CodeCoverage.Versioning.Versions.VersioningException>();
    }

    [Fact]
    public async Task Private_assets_references_are_dev_dependencies()
    {
        var graph = await Build(new InMemoryTree()
            .Add("Directory.Packages.props", "<Project><ItemGroup><PackageVersion Include=\"Polly\" Version=\"9.0.0-beta.1\" /></ItemGroup></Project>")
            .Add("Lib/Lib.csproj", Library(extra: """
                <ItemGroup>
                  <PackageReference Include="Polly" />
                  <PackageReference Include="MinVer" Version="6.0.0"><PrivateAssets>all</PrivateAssets></PackageReference>
                </ItemGroup>
                """)));

        graph.ById("Lib")!.Dependencies.Should().BeEquivalentTo([
            new PackageDependency("Polly", "9.0.0-beta.1", DependencyKind.Runtime),
            new PackageDependency("MinVer", "6.0.0", DependencyKind.Dev),
        ]);
    }

    [Fact]
    public async Task An_npm_workspace_and_an_nx_angular_library_are_packages()
    {
        var graph = await Build(new InMemoryTree()
            .Add("package.json", """{ "name": "root", "private": true, "workspaces": ["packages/*"] }""")
            .Add("packages/util/package.json", """{ "name": "@acme/util", "version": "0.0.0-PLACEHOLDER" }""")
            .Add("packages/app/package.json", """{ "name": "@acme/app", "private": true, "dependencies": { "@acme/util": "*" } }""")
            .Add("libs/ng-acme/project.json", "{}")
            .Add("libs/ng-acme/ng-package.json", """{ "dest": "../../dist/libs/ng-acme" }""")
            .Add("libs/ng-acme/package.json", """{ "name": "@acme/ng-acme", "peerDependencies": { "@angular/core": "^22.0.0", "@acme/util": "^1.0.0" } }""")
            .Add("node_modules/@acme/util/package.json", """{ "name": "@acme/util" }""")
            .Add("package-lock.json", """{ "lockfileVersion": 3, "packages": { "node_modules/@angular/core": { "version": "22.1.3" } } }"""));

        graph.Packages.Select(p => p.Id).Should().BeEquivalentTo(["@acme/util", "@acme/ng-acme"]);
        var ng = graph.ById("@acme/ng-acme")!;
        ng.BuildOutput.Should().Be("dist/libs/ng-acme");
        ng.FrameworkPackage.Should().Be("@angular/core");
        graph.AnchorOf(ng).Major.Should().Be(22);
        graph.InternalDependencies(ng).Select(d => d.Project.Id).Should().BeEquivalentTo(["@acme/util"]);
        graph.AnchorOf(graph.ById("@acme/util")!).IsFree.Should().BeTrue();
    }

    /// <summary>
    /// mintplayer-ng-bootstrap #446: <c>@nx/js:tsc</c> and <c>@nx/vite:build</c> copy package.json into the build target's
    /// outputPath, which the action must stamp too, or that copy publishes as the placeholder.
    /// </summary>
    [Fact]
    public async Task An_nx_build_targets_outputPath_is_the_build_output()
    {
        var graph = await Build(new InMemoryTree()
            .Add("package.json", """{ "name": "root", "private": true }""")
            .Add("libs/tsc/project.json", """{ "targets": { "build": { "executor": "@nx/js:tsc", "options": { "outputPath": "dist/libs/tsc" } } } }""")
            .Add("libs/tsc/package.json", """{ "name": "@acme/tsc", "version": "0.0.0-PLACEHOLDER" }""")
            .Add("libs/vite/project.json", """{ "targets": { "build": { "executor": "@nx/vite:build", "options": { "outputPath": "{workspaceRoot}/dist/{projectRoot}" } } } }""")
            .Add("libs/vite/package.json", """{ "name": "@acme/vite", "version": "0.0.0-PLACEHOLDER" }""")
            .Add("libs/none/project.json", """{ "targets": { "test": { "options": { "outputPath": "dist/never" } } } }""")
            .Add("libs/none/package.json", """{ "name": "@acme/none", "version": "0.0.0-PLACEHOLDER" }"""));

        graph.ById("@acme/tsc")!.BuildOutput.Should().Be("dist/libs/tsc");
        graph.ById("@acme/vite")!.BuildOutput.Should().Be("dist/libs/vite");
        graph.ById("@acme/none")!.BuildOutput.Should().BeNull();
    }

    [Fact]
    public async Task An_nx_outputPath_outside_the_repository_is_a_warning()
    {
        var graph = await Build(new InMemoryTree()
            .Add("package.json", """{ "name": "root", "private": true }""")
            .Add("libs/x/project.json", """{ "targets": { "build": { "options": { "outputPath": "../../../outside" } } } }""")
            .Add("libs/x/package.json", """{ "name": "@acme/x" }"""));

        graph.ById("@acme/x")!.BuildOutput.Should().BeNull();
        graph.Warnings.Should().ContainSingle().Which.Should().Contain("outputPath '../../../outside' is outside the repository");
    }

    [Fact]
    public async Task A_packed_package_missing_from_the_graph_fails_the_cross_check()
    {
        var graph = await Build(new InMemoryTree().Add("Lib/Lib.csproj", Library()));

        graph.CrossCheck(["Lib"]);
        ((Action)(() => graph.CrossCheck(["Lib", "Ghost"]))).Should().Throw<GraphException>();
    }

    [Fact]
    public async Task A_project_reference_escaping_the_repository_is_refused()
        => await ((Func<Task>)(() => Build(new InMemoryTree()
            .Add("Lib/Lib.csproj", Library(extra: "<ItemGroup><ProjectReference Include=\"..\\..\\..\\etc\\x.csproj\" /></ItemGroup>"))))).Should().ThrowAsync<GraphException>();

    [Fact]
    public async Task An_id_two_projects_declare_is_versioned_by_neither_and_says_where()
    {
        var graph = await Build(new InMemoryTree()
            .Add("src/Lib/Lib.csproj", Library())
            .Add("test/fixtures/Lib/Lib.csproj", Library())
            .Add("src/Other/Other.csproj", Library()));

        graph.Packages.Select(p => p.Id).Should().BeEquivalentTo(["Other"]);
        graph.Warnings.Should().Contain(w => w.Contains("src/Lib/Lib.csproj") && w.Contains("test/fixtures/Lib/Lib.csproj"));
    }

    [Fact]
    public async Task Projects_matching_versioning_ignore_are_left_out_of_the_graph()
    {
        var graph = await ProjectGraphBuilder.BuildAsync(new InMemoryTree()
            .Add("src/Lib/Lib.csproj", Library())
            .Add("test/fixtures/Lib/Lib.csproj", Library())
            .Add("test/fixtures/web/package.json", """{ "name": "@acme/web", "version": "1.0.0" }""")
            .Add("package.json", """{ "name": "root", "private": true, "workspaces": ["test/fixtures/*"] }"""),
            ["**/*.md", "**/fixtures/**"]);

        graph.Projects.Select(p => p.Path).Should().NotContain(p => p.StartsWith("test/", StringComparison.Ordinal));
        graph.Packages.Select(p => p.Id).Should().BeEquivalentTo(["Lib"]);
        graph.Warnings.Should().NotContain(w => w.Contains("more than one"));
    }

    [Fact]
    public async Task Symbolic_links_are_never_read()
    {
        var graph = await Build(new InMemoryTree()
            .AddSymlink("Evil/Evil.csproj", "/etc/passwd")
            .Add("Lib/Lib.csproj", Library()));

        graph.Projects.Select(p => p.Path).Should().BeEquivalentTo(["Lib/Lib.csproj"]);
        graph.Warnings.Should().Contain(w => w.Contains("Evil/Evil.csproj"));
    }

    [Fact]
    public async Task A_project_reference_to_a_symbolic_link_is_refused()
        => await ((Func<Task>)(() => Build(new InMemoryTree()
            .AddSymlink("Other/Other.csproj", "../Lib/Lib.csproj")
            .Add("Lib/Lib.csproj", Library(extra: "<ItemGroup><ProjectReference Include=\"..\\Other\\Other.csproj\" /></ItemGroup>"))))).Should().ThrowAsync<GraphException>();

    [Fact]
    public async Task An_oversized_project_file_is_refused()
        => await ((Func<Task>)(() => Build(new InMemoryTree()
            .Add("Lib/Lib.csproj", new byte[Limits.MaxManifestBytes + 1])))).Should().ThrowAsync<GraphException>();

    [Fact]
    public async Task A_dtd_is_never_processed()
    {
        var graph = await Build(new InMemoryTree()
            .Add("Lib/Lib.csproj", "<!DOCTYPE Project [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><Project><PropertyGroup><PackageId>&x;</PackageId></PropertyGroup></Project>"));

        graph.Projects.Should().BeEmpty();
        graph.Warnings.Should().Contain(w => w.Contains("not readable XML"));
    }

    [Theory]
    [InlineData("**/**/**/**/**/**/**/**/**/**/**/**/**/**/**/**/**/**/**/**/x")]
    [InlineData("*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*b")]
    public void A_glob_bomb_stays_bounded(string glob)
    {
        var path = string.Join('/', Enumerable.Repeat(new string('a', 60), 30));
        var watch = Stopwatch.StartNew();
        new PathGlob(glob).Matches(path).Should().BeFalse();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData("packages/*", "packages/a/package.json", false)]
    [InlineData("packages/*/package.json", "packages/a/package.json", true)]
    [InlineData("libs/**/package.json", "libs/x/y/package.json", true)]
    [InlineData("*.md", "docs/a/readme.md", true)]
    [InlineData("./src/*.cs", "src/a.cs", true)]
    [InlineData("src/?.cs", "src/ab.cs", false)]
    public void Globs_match_by_segment(string glob, string path, bool matches)
        => new PathGlob(glob).Matches(path).Should().Be(matches);

    [Theory]
    [InlineData("a**b")]
    [InlineData("")]
    public void Malformed_globs_are_refused(string glob)
        => ((Action)(() => new PathGlob(glob))).Should().Throw<GraphException>();

    [Fact]
    public void Too_long_and_too_deep_globs_are_refused()
    {
        ((Action)(() => new PathGlob(new string('a', Limits.MaxGlobLength + 1)))).Should().Throw<GraphException>();
        ((Action)(() => new PathGlob(string.Join('/', Enumerable.Repeat("a", Limits.MaxGlobSegments + 1))))).Should().Throw<GraphException>();
    }

    [Theory]
    [InlineData("src", "../x", "x")]
    [InlineData("src/a", "..\\..\\b\\c.csproj", "b/c.csproj")]
    [InlineData("src", "../../x", null)]
    [InlineData("", "/etc/passwd", null)]
    [InlineData("", "C:\\Windows", null)]
    public void Paths_resolve_inside_the_repository(string directory, string relative, string? expected)
        => RepoPath.Resolve(directory, relative).Should().Be(expected);

    [Fact]
    public async Task A_git_tree_reads_the_graph_at_a_commit()
    {
        var work = Directory.CreateTempSubdirectory("versioning-graph-");
        try
        {
            Directory.CreateDirectory(Path.Combine(work.FullName, "Lib"));
            await File.WriteAllTextAsync(Path.Combine(work.FullName, "Lib", "Lib.csproj"), Library());
            var git = new GitCli(Path.Combine(work.FullName, ".git"));
            Run(work.FullName, "init", "-q");
            Run(work.FullName, "add", ".");
            Run(work.FullName, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "init");
            var head = (await git.RunAsync(["rev-parse", "HEAD"])).EnsureSuccess("rev-parse").Text.Trim();

            var tree = await GitTree.OpenAsync(git, head);
            var graph = await ProjectGraphBuilder.BuildAsync(tree);

            graph.Packages.Select(p => p.Id).Should().BeEquivalentTo(["Lib"]);
            graph.AnchorOf(graph.ById("Lib")!).Major.Should().Be(10);
        }
        finally
        {
            foreach (var file in work.EnumerateFiles("*", SearchOption.AllDirectories)) file.Attributes = FileAttributes.Normal;
            work.Delete(recursive: true);
        }

        static void Run(string directory, params string[] arguments)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            process.WaitForExit();
            process.ExitCode.Should().Be(0);
        }
    }
}
