using System.Diagnostics;
using CodeCoverage.Versioning.Build;
using CodeCoverage.Versioning.Git;
using CodeCoverage.Versioning.Graph;
using CodeCoverage.Versioning.Releases;

namespace CodeCoverage.Versioning.Tests.Build;

/// <summary>#153 M7 (D5, D6, S7): what the action reads from a built checkout, in both modes.</summary>
public class BuildManifestTests : IDisposable
{
    private const string RegistryHash = "sha256:4444444444444444444444444444444444444444444444444444444444444444";

    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("versioning-manifest-");

    private sealed class FakeRegistry : IPackageRegistry
    {
        public List<string> Asked { get; } = [];

        public Task<string?> ApiHashAsync(ProjectNode package, string version, CancellationToken cancellationToken = default)
        {
            Asked.Add($"{package.Id} {version}");
            return Task.FromResult<string?>(RegistryHash);
        }
    }

    private void Write(string path, string text)
    {
        var full = Path.Combine(root.FullName, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private string Git(params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root.FullName, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-c", "user.name=t", "-c", "user.email=t@t" }.Concat(args)) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.Should().Be(0, string.Join(' ', args));
        return output.Trim();
    }

    [Fact]
    public async Task Reads_the_built_api_of_each_package_and_asks_the_registry_only_behind_a_hand_made_tag()
    {
        // The test assembly stands in for Acme.Core's build output: the lister reads any assembly's metadata.
        var assembly = Path.GetFileNameWithoutExtension(typeof(BuildManifestTests).Assembly.Location);
        Write("src/Core/Core.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk\">\n<PropertyGroup><TargetFramework>net10.0</TargetFramework><PackageId>Acme.Core</PackageId><AssemblyName>{assembly}</AssemblyName></PropertyGroup>\n</Project>\n");
        Write("package.json", "{ \"private\": true, \"workspaces\": [\"packages/*\"] }\n");
        Write("packages/util/package.json", "{ \"name\": \"@acme/util\", \"version\": \"0.0.0\" }\n");
        Write("packages/util/index.d.ts", "export declare function util(): void;\n");
        Write("packages/bare/package.json", "{ \"name\": \"@acme/bare\", \"version\": \"0.0.0\" }\n");
        Git("init", "-q", "-b", "main");
        Git("add", ".");
        Git("commit", "-q", "-m", "packages");
        var commit = Git("rev-parse", "HEAD");
        Git("tag", "@acme/util/v1.0.0");
        Git("tag", "-a", "-m", "api-hash: " + RegistryHash, "Acme.Core/v1.0.0");
        Directory.CreateDirectory(Path.Combine(root.FullName, "src/Core/bin/Release/net10.0"));
        File.Copy(typeof(BuildManifestTests).Assembly.Location, Path.Combine(root.FullName, "src/Core/bin/Release/net10.0", assembly + ".dll"));

        var registry = new FakeRegistry();
        var result = await BuildManifest.CollectAsync(new GitCli(Path.Combine(root.FullName, ".git")),
            new BuildManifestRequest(root.FullName, commit, "Release", BranchMajor: null, registry));

        var packages = result.Manifest.Packages!.ToDictionary(p => p.Id!);
        packages.Keys.Should().BeEquivalentTo(["Acme.Core", "@acme/util", "@acme/bare"]);
        (packages["Acme.Core"].Packed, packages["Acme.Core"].ApiHash is null, packages["Acme.Core"].PreviousApiHash).Should().Be((true, false, null),
            "an annotated tag carries its own hash, so the registry is not asked");
        packages["Acme.Core"].TargetFrameworks.Should().Equal("net10.0");
        (packages["@acme/util"].Packed, packages["@acme/util"].ApiHash is null, packages["@acme/util"].PreviousApiHash).Should().Be((true, false, RegistryHash));
        (packages["@acme/bare"].Packed, packages["@acme/bare"].ApiHash).Should().Be((false, null));
        registry.Asked.Should().Equal("@acme/util 1.0.0");
        result.Notes.Should().ContainSingle().Which.Should().StartWith("@acme/bare: no .d.ts");
        result.Manifest.Validate().Should().BeEmpty();
    }

    /// <summary>
    /// #576 D2: a tag whose hash another listing version made can't be compared with this build's, so the last release is
    /// hashed again from the registry. A tag with this listing's hash needs no registry.
    /// </summary>
    [Fact]
    public async Task A_tag_hashed_by_another_listing_version_asks_the_registry()
    {
        Write("package.json", "{ \"private\": true, \"workspaces\": [\"packages/*\"] }\n");
        Write("packages/old/package.json", "{ \"name\": \"@acme/old\", \"version\": \"0.0.0\" }\n");
        Write("packages/old/index.d.ts", "export declare const styles = \"a { b: c; }\";\n");
        Write("packages/new/package.json", "{ \"name\": \"@acme/new\", \"version\": \"0.0.0\" }\n");
        Write("packages/new/index.d.ts", "export declare function render(): void;\n");
        Git("init", "-q", "-b", "main");
        Git("add", ".");
        Git("commit", "-q", "-m", "packages");
        var commit = Git("rev-parse", "HEAD");
        Git("tag", "-a", "-m", "api-hash: sha256:" + new string('1', 64), "@acme/old/v1.0.0");
        Git("tag", "-a", "-m", "api-hash: v2:sha256:" + new string('2', 64), "@acme/new/v1.0.0");

        var registry = new FakeRegistry();
        var result = await CollectAsync(commit, registry);

        var packages = result.Manifest.Packages!.ToDictionary(p => p.Id!);
        registry.Asked.Should().Equal("@acme/old 1.0.0");
        packages["@acme/old"].PreviousApiHash.Should().Be(RegistryHash);
        packages["@acme/new"].PreviousApiHash.Should().BeNull();
        packages["@acme/new"].ApiHash.Should().StartWith("v2:sha256:");
        result.Manifest.Validate().Should().BeEmpty();
    }

    private sealed class AnsweringRegistry(Func<ProjectNode, string?> answer) : IPackageRegistry
    {
        public Task<string?> ApiHashAsync(ProjectNode package, string version, CancellationToken cancellationToken = default)
            => Task.FromResult(answer(package));
    }

    /// <summary>A .NET package built from the test assembly, tagged by hand at 1.0.0, with the given restore output.</summary>
    private string BuiltDotnetPackage(string assetsJson)
    {
        var assembly = Path.GetFileNameWithoutExtension(typeof(BuildManifestTests).Assembly.Location);
        Write("src/Core/Core.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk\">\n<PropertyGroup><TargetFramework>net10.0</TargetFramework><PackageId>Acme.Core</PackageId><AssemblyName>{assembly}</AssemblyName></PropertyGroup>\n"
            + "<ItemGroup><PackageReference Include=\"Direct.Dep\" Version=\"1.0.0\" /></ItemGroup>\n</Project>\n");
        Write("src/Core/obj/project.assets.json", assetsJson);
        Git("init", "-q", "-b", "main");
        Git("add", "src/Core/Core.csproj");
        Git("commit", "-q", "-m", "core");
        Git("tag", "Acme.Core/v1.0.0");
        Directory.CreateDirectory(Path.Combine(root.FullName, "src/Core/bin/Release/net10.0"));
        File.Copy(typeof(BuildManifestTests).Assembly.Location, Path.Combine(root.FullName, "src/Core/bin/Release/net10.0", assembly + ".dll"));
        return Git("rev-parse", "HEAD");
    }

    private Task<BuildManifestResult> CollectAsync(string commit, IPackageRegistry? registry)
        => BuildManifest.CollectAsync(new GitCli(Path.Combine(root.FullName, ".git")), new BuildManifestRequest(root.FullName, commit, "Release", BranchMajor: null, registry));

    [Fact]
    public async Task Restore_output_gives_the_resolved_package_versions_and_nothing_else()
    {
        var commit = BuiltDotnetPackage("""
            { "libraries": {
                "Newtonsoft.Json/13.0.3": { "type": "package" },
                "Acme.Sibling/1.0.0": { "type": "project" },
                "NoVersion": { "type": "package" },
                "/1.0.0": { "type": "package" },
                "Bad Id/1.0.0": { "type": "package" },
                "Untyped/1.0.0": { }
            } }
            """);

        var result = await CollectAsync(commit, registry: null);

        result.Manifest.Packages!.Single().ResolvedVersions.Should().BeEquivalentTo(new Dictionary<string, string> { ["Newtonsoft.Json"] = "13.0.3" });
    }

    [Fact]
    public async Task Restore_output_without_libraries_resolves_nothing()
    {
        var commit = BuiltDotnetPackage("""{ "libraries": [] }""");

        (await CollectAsync(commit, registry: null)).Manifest.Packages!.Single().ResolvedVersions.Should().BeNull();
    }

    [Fact]
    public async Task Too_many_resolved_versions_keep_only_the_packages_own_dependencies()
    {
        var libraries = Enumerable.Range(0, ReleaseManifest.MaxResolvedVersions).Select(i => $"\"Transitive.P{i}/1.0.{i}\": {{ \"type\": \"package\" }}")
            .Append("\"Direct.Dep/1.2.3\": { \"type\": \"package\" }");
        var commit = BuiltDotnetPackage("{ \"libraries\": { " + string.Join(", ", libraries) + " } }");

        var resolved = (await CollectAsync(commit, registry: null)).Manifest.Packages!.Single().ResolvedVersions;

        resolved.Should().BeEquivalentTo(new Dictionary<string, string> { ["Direct.Dep"] = "1.2.3" });
    }

    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(InvalidDataException))]
    [InlineData(typeof(TaskCanceledException))]
    public async Task A_registry_that_fails_counts_the_api_as_changed(Type error)
    {
        var commit = BuiltDotnetPackage("{}");

        var result = await CollectAsync(commit, new AnsweringRegistry(_ => throw (Exception)Activator.CreateInstance(error)!));

        result.Manifest.Packages!.Single().PreviousApiHash.Should().BeNull();
        result.Notes.Should().Equal($"Acme.Core 1.0.0 could not be read from the registry ({error.Name}); its API counts as changed.");
    }

    [Fact]
    public async Task A_version_missing_from_the_registry_counts_the_api_as_changed()
    {
        var commit = BuiltDotnetPackage("{}");

        var result = await CollectAsync(commit, new AnsweringRegistry(_ => null));

        result.Notes.Should().Equal("Acme.Core 1.0.0 is not on the public registry; its API counts as changed.");
    }

    [Fact]
    public async Task A_dotnet_package_that_was_not_built_has_no_api_and_says_why()
    {
        Write("src/Lib/Lib.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n<PropertyGroup><TargetFramework>net10.0</TargetFramework><PackageId>Acme.Lib</PackageId></PropertyGroup>\n</Project>\n");
        Git("init", "-q", "-b", "main");
        Git("add", ".");
        Git("commit", "-q", "-m", "lib");

        var result = await CollectAsync(Git("rev-parse", "HEAD"), registry: null);

        var package = result.Manifest.Packages!.Single();
        (package.ApiHash, package.Packed, result.Manifest.Sdk).Should().Be((null, false, null));
        // The assembly is named after the project file, not the package id.
        result.Notes.Should().Equal("Acme.Lib: no Lib.dll under bin/Release; built with another configuration or output path? Its API counts as changed.");
    }

    public void Dispose()
    {
        foreach (var file in root.EnumerateFiles("*", SearchOption.AllDirectories)) file.Attributes = FileAttributes.Normal;
        root.Delete(recursive: true);
    }
}
