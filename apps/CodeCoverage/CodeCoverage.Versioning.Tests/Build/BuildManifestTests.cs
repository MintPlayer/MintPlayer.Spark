using System.Diagnostics;
using CodeCoverage.Versioning.Build;
using CodeCoverage.Versioning.Git;
using CodeCoverage.Versioning.Graph;

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

    public void Dispose()
    {
        foreach (var file in root.EnumerateFiles("*", SearchOption.AllDirectories)) file.Attributes = FileAttributes.Normal;
        root.Delete(recursive: true);
    }
}
