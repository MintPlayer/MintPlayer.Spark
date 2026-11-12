using CodeCoverage.Versioning.Git;
using CodeCoverage.Versioning.Releases;
using static CodeCoverage.Versioning.Tests.Releases.ReleasePlannerTests;

namespace CodeCoverage.Versioning.Tests.Releases;

/// <summary>
/// #638 D5-D7: the onboarding tags every version a package declared in its history at its earliest occurrence, on the
/// line of its major when that line exists, so the pack on the line (<c>tag --merged HEAD</c>) reaches it.
/// </summary>
public class HistoryBackfillTests
{
    private static string Project(string version, string tfm = "net10.0")
        => $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>{tfm}</TargetFramework><Version>{version}</Version></PropertyGroup></Project>";

    [Fact]
    public async Task Every_declared_version_is_tagged_at_its_earliest_occurrence_on_the_line_of_its_major()
    {
        var work = Directory.CreateTempSubdirectory("versioning-backfill-");
        try
        {
            var root = work.FullName;
            Directory.CreateDirectory(Path.Combine(root, "Hsts"));
            Run(root, "init", "-q", "-b", "main");
            var c1 = await CommitAsync(root, "Hsts/Hsts.csproj", Project("10.0.0"), "hsts 10.0.0");
            var c2 = await CommitAsync(root, "README.md", "readme", "docs");
            var c3 = await CommitAsync(root, "Hsts/Hsts.csproj", Project("10.0.1"), "hsts 10.0.1");
            Run(root, "branch", "release/dotnet-10.x");
            await CommitAsync(root, "Hsts/Hsts.csproj", Project("0.0.0-PLACEHOLDER"), "versioning onboarding");
            var c5 = await CommitAsync(root, "Hsts/Hsts.csproj", Project("0.0.0-PLACEHOLDER", "net11.0"), "net11.0");
            Run(root, "tag", "Hsts/v11.0.0", c5);

            Run(root, "switch", "-q", "release/dotnet-10.x");
            var c6 = await CommitAsync(root, "Hsts/Hsts.csproj", Project("10.0.2"), "hsts 10.0.2 on the line");
            Run(root, "switch", "-q", "-c", "release/dotnet-7.x", c1);
            var c7 = await CommitAsync(root, "Hsts/Hsts.csproj", Project("7.3.0"), "hsts 7.3.0, declared only on its line");
            Run(root, "switch", "-q", "-c", "feature/next", c3);
            await CommitAsync(root, "Hsts/Hsts.csproj", Project("99.0.0"), "a version that never shipped");
            Run(root, "switch", "-q", "main");

            var git = new GitCli(Path.Combine(root, ".git"));
            var settings = new VersioningConfig { Lines = new VersioningLinesConfig() }.ToSettings();

            var plan = await HistoryBackfill.PlanAsync(git, "main", settings);

            plan.Tags.Select(t => $"{t.Tag.Name}@{t.Commit}@{t.Branch}").Should().BeEquivalentTo([
                $"Hsts/v10.0.0@{c1}@release/dotnet-10.x",
                $"Hsts/v10.0.1@{c3}@release/dotnet-10.x",
                $"Hsts/v10.0.2@{c6}@release/dotnet-10.x",
                $"Hsts/v7.3.0@{c7}@release/dotnet-7.x",
            ]);
            // Feature branches are never walked (D6).
            plan.Branches.Should().Equal("main", "release/dotnet-10.x", "release/dotnet-7.x");
            c2.Should().NotBeNull();

            // Without lines, only the default branch is walked and it holds every tag.
            var withoutLines = await HistoryBackfill.PlanAsync(git, "main", new VersioningConfig().ToSettings());
            withoutLines.Tags.Select(t => $"{t.Tag.Name}@{t.Commit}@{t.Branch}").Should().BeEquivalentTo([
                $"Hsts/v10.0.0@{c1}@main",
                $"Hsts/v10.0.1@{c3}@main",
            ]);

            // Once tagged, nothing is planned again (D8).
            foreach (var tag in plan.Tags) Run(root, "tag", tag.Tag.Name, tag.Commit);
            (await HistoryBackfill.PlanAsync(git, "main", settings)).Tags.Should().BeEmpty();
        }
        finally
        {
            foreach (var file in work.EnumerateFiles("*", SearchOption.AllDirectories)) file.Attributes = FileAttributes.Normal;
            work.Delete(recursive: true);
        }
    }

    private static async Task<string> CommitAsync(string root, string path, string content, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(root, path), content);
        Run(root, "add", ".");
        Run(root, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", message);
        var git = new GitCli(Path.Combine(root, ".git"));
        return (await git.RunAsync(["rev-parse", "HEAD"])).EnsureSuccess("rev-parse").Text.Trim();
    }
}
