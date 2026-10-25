using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;

namespace CodeCoverage.Versioning.Tests.MSBuild;

/// <summary>
/// #544 R8: MintPlayer.Versioning.MSBuild's real targets under a real <c>dotnet pack</c>. The task-only tests could not
/// see what reached two releases: an explicit <c>-p:Version</c> ignored, and a multi-targeted outer build packing the
/// placeholder. A repository with a library (A) referencing another (B), and a multi-targeted one (M), imports the
/// targets from a <c>Directory.Build.targets</c>, as Spark imports them from source.
/// </summary>
public class RealPackTests(RealPackTests.Repo repo) : IClassFixture<RealPackTests.Repo>
{
    private const string Push = "push";
    private const string PullRequest = "pull_request";

    [Fact]
    public void An_explicit_version_packs_at_that_version_without_a_tag()
    {
        foreach (var eventName in new[] { Push, PullRequest })
        {
            repo.Pack("A/A.csproj", eventName, "-p:Version=9.9.9").ShouldPack("A.9.9.9.nupkg");
            repo.Pack("M/M.csproj", eventName, "-p:Version=9.9.9").ShouldPack("M.9.9.9.nupkg");
        }
    }

    [Fact]
    public void A_package_version_alone_sets_the_assembly_version_too()
    {
        var result = repo.Pack("A/A.csproj", Push, "-p:PackageVersion=9.8.7");

        result.ShouldPack("A.9.8.7.nupkg");
        result.Nuspec("A.9.8.7.nupkg").Should().Contain("<dependency id=\"B\" version=\"9.8.7\"");
        result.AssemblyVersion("A.9.8.7.nupkg", "A.dll").Should().Be(new Version(9, 8, 7, 0));
    }

    [Fact]
    public void An_untagged_project_packed_on_its_own_fails_on_a_push_build()
    {
        var result = repo.Pack("A/A.csproj", Push);

        result.ExitCode.Should().NotBe(0);
        result.Output.Should().Contain("MPV005");
        result.Packages.Should().BeEmpty();
    }

    [Fact]
    public void An_untagged_solution_pack_on_a_push_build_drops_the_projects_with_a_warning()
    {
        var result = repo.Pack("S.sln", Push);

        result.ExitCode.Should().Be(0, result.Output);
        result.Output.Should().Contain("MPV001").And.NotContain("MPV005");
        result.Packages.Should().BeEmpty();
    }

    [Fact]
    public void An_untagged_project_packs_at_the_untagged_version_locally()
    {
        repo.Pack("A/A.csproj", eventName: null).ShouldPack("A.0.0.0-untagged.nupkg");
    }

    [Fact]
    public void Tagged_projects_pack_at_their_tags_and_an_explicit_version_still_wins()
    {
        repo.Tag("A/v1.2.3", "B/v2.0.0", "M/v3.0.0");
        try
        {
            var solution = repo.Pack("S.sln", Push);
            solution.ShouldPack("A.1.2.3.nupkg", "B.2.0.0.nupkg", "M.3.0.0.nupkg");
            solution.Nuspec("A.1.2.3.nupkg").Should().Contain("<dependency id=\"B\" version=\"2.0.0\"");

            repo.Pack("A/A.csproj", Push, "-p:Version=9.9.9").ShouldPack("A.9.9.9.nupkg");
        }
        finally
        {
            repo.Untag("A/v1.2.3", "B/v2.0.0", "M/v3.0.0");
        }
    }

    public sealed record PackResult(int ExitCode, string Output, string Folder)
    {
        public IReadOnlyList<string> Packages => Directory.Exists(Folder)
            ? Directory.GetFiles(Folder, "*.nupkg").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList()!
            : [];

        public void ShouldPack(params string[] packages)
        {
            ExitCode.Should().Be(0, Output);
            string.Join(" ", Packages).Should().Be(string.Join(" ", packages.Order(StringComparer.Ordinal)), Output);
        }

        public string Nuspec(string package)
        {
            using var zip = ZipFile.OpenRead(Path.Combine(Folder, package));
            using var reader = new StreamReader(zip.Entries.Single(e => e.FullName.EndsWith(".nuspec", StringComparison.Ordinal)).Open());
            return reader.ReadToEnd();
        }

        public Version? AssemblyVersion(string package, string assembly)
        {
            using var zip = ZipFile.OpenRead(Path.Combine(Folder, package));
            var file = Path.Combine(Folder, assembly);
            zip.Entries.Single(e => e.Name == assembly).ExtractToFile(file, overwrite: true);
            return AssemblyName.GetAssemblyName(file).Version;
        }
    }

    /// <summary>The repository, restored once for the class; every pack writes a fresh output folder.</summary>
    public sealed class Repo : IDisposable
    {
        private readonly DirectoryInfo work = Directory.CreateTempSubdirectory("versioning-realpack-");
        private int packs;

        public Repo()
        {
            var tfm = $"net{Environment.Version.Major}.0";
            var source = Path.Combine(AppContext.BaseDirectory, "RealPack");
            File.Copy(Path.Combine(source, "MintPlayer.Versioning.MSBuild.targets"), Path.Combine(work.FullName, "mpv.targets"));
            File.Copy(Path.Combine(source, "MintPlayer.Versioning.MSBuild.cs"), Path.Combine(work.FullName, "mpv.cs"));
            Write("Directory.Build.targets",
                "<Project><PropertyGroup><MintPlayerVersioningTaskSource>$(MSBuildThisFileDirectory)mpv.cs</MintPlayerVersioningTaskSource></PropertyGroup>"
                + "<Import Project=\"$(MSBuildThisFileDirectory)mpv.targets\" /></Project>");
            Write("B/B.csproj", Library("B", $"<TargetFramework>{tfm}</TargetFramework>"));
            Write("A/A.csproj", Library("A", $"<TargetFramework>{tfm}</TargetFramework>", "<ItemGroup><ProjectReference Include=\"..\\B\\B.csproj\" /></ItemGroup>"));
            Write("M/M.csproj", Library("M", $"<TargetFrameworks>netstandard2.0;{tfm}</TargetFrameworks>"));
            foreach (var project in new[] { "A", "B", "M" }) Write($"{project}/C.cs", "public class C { }");
            Write("S.sln", Solution());

            Git("init", "-q", "-b", "main");
            Git("add", "-A");
            Git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "init");
            var restore = Dotnet(["restore", "S.sln"], eventName: null);
            if (restore.ExitCode != 0) throw new InvalidOperationException("restore failed:\n" + restore.Output);
        }

        public PackResult Pack(string target, string? eventName, params string[] arguments)
        {
            var folder = Path.Combine(work.FullName, "out" + Interlocked.Increment(ref packs));
            var (exitCode, output) = Dotnet(["pack", target, "--no-restore", "-o", folder, .. arguments], eventName);
            return new PackResult(exitCode, output, folder);
        }

        public void Tag(params string[] names)
        {
            foreach (var name in names) Git("-c", "user.name=t", "-c", "user.email=t@t", "tag", "-a", "-m", "t", name);
        }

        public void Untag(params string[] names) => Git(["tag", "--delete", .. names]);

        private static string Library(string id, string frameworks, string items = "")
            => $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>{frameworks}<PackageId>{id}</PackageId><Version>0.0.0-PLACEHOLDER</Version></PropertyGroup>{items}</Project>";

        private static string Solution()
        {
            var text = new System.Text.StringBuilder("Microsoft Visual Studio Solution File, Format Version 12.00\n");
            var projects = new[] { ("A", "11111111-1111-1111-1111-111111111111"), ("B", "22222222-2222-2222-2222-222222222222"), ("M", "33333333-3333-3333-3333-333333333333") };
            foreach (var (name, guid) in projects)
                text.Append($"Project(\"{{9A19103F-16F7-4668-BE54-9A1E7A4F7556}}\") = \"{name}\", \"{name}\\{name}.csproj\", \"{{{guid}}}\"\nEndProject\n");
            // `dotnet pack` builds Release.
            text.Append("Global\n\tGlobalSection(SolutionConfigurationPlatforms) = preSolution\n\t\tRelease|Any CPU = Release|Any CPU\n\tEndGlobalSection\n");
            text.Append("\tGlobalSection(ProjectConfigurationPlatforms) = postSolution\n");
            foreach (var (_, guid) in projects)
                text.Append($"\t\t{{{guid}}}.Release|Any CPU.ActiveCfg = Release|Any CPU\n\t\t{{{guid}}}.Release|Any CPU.Build.0 = Release|Any CPU\n");
            return text.Append("\tEndGlobalSection\nEndGlobal\n").ToString();
        }

        private void Write(string path, string content)
        {
            var file = Path.Combine(work.FullName, path);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, content);
        }

        private void Git(params string[] arguments)
        {
            var (exitCode, output) = Run("git", arguments, null);
            if (exitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed:\n{output}");
        }

        private (int ExitCode, string Output) Dotnet(string[] arguments, string? eventName) => Run("dotnet", arguments, start =>
        {
            // The build must not see the test host's MSBuild (`dotnet test` sets MSBuild* variables) or its CI event: each
            // case states the event it simulates, and null is a local build.
            foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase)).ToList())
                start.Environment.Remove(name);
            start.Environment["GITHUB_ACTIONS"] = eventName is null ? "" : "true";
            start.Environment["GITHUB_EVENT_NAME"] = eventName ?? "";
            start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            start.Environment["DOTNET_NOLOGO"] = "1";
            start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        });

        private (int ExitCode, string Output) Run(string file, IEnumerable<string> arguments, Action<ProcessStartInfo>? configure)
        {
            var start = new ProcessStartInfo(file) { WorkingDirectory = work.FullName, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            configure?.Invoke(start);
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(TimeSpan.FromMinutes(3))) process.Kill(entireProcessTree: true);
            process.WaitForExit();
            return (process.ExitCode, output + error.Result);
        }

        public void Dispose()
        {
            foreach (var file in work.EnumerateFiles("*", SearchOption.AllDirectories)) file.Attributes = FileAttributes.Normal;
            try { work.Delete(recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
