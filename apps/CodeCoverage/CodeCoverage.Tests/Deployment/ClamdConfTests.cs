using Xunit;

namespace CodeCoverage.Tests.Deployment;

/// <summary>
/// The production clamd.conf (<c>apps/CodeCoverage/ops/clamd.conf</c>, #700 M11) against the package's
/// (<c>libs/files/MintPlayer.Spark.Files.ClamAV/clamd.conf</c>): every directive of the package's file is here with its value, so
/// a fail-closed limit tightened in the package cannot be left behind in production, and the memory setting the shared VPS needs
/// stays on.
/// </summary>
public class ClamdConfTests
{
    private static readonly string Root = RepositoryRoot();

    [Fact]
    public void Production_conf_carries_every_directive_of_the_package_conf()
    {
        var package = Directives(Path.Combine(Root, "libs", "files", "MintPlayer.Spark.Files.ClamAV", "clamd.conf"));
        var production = Directives(Path.Combine(Root, "apps", "CodeCoverage", "ops", "clamd.conf"));

        package.Should().NotBeEmpty();
        foreach (var (name, value) in package)
            production.GetValueOrDefault(name).Should().Be(value, $"the production clamd.conf must carry '{name} {value}'");
    }

    [Fact]
    public void Production_conf_reloads_the_signatures_without_a_second_copy_in_memory()
        => Directives(Path.Combine(Root, "apps", "CodeCoverage", "ops", "clamd.conf"))
            .GetValueOrDefault("ConcurrentDatabaseReload").Should().Be("no");

    /// <summary>The <c>Name value</c> lines, comments and blanks skipped. A directive given twice fails: clamd would take the last.</summary>
    private static Dictionary<string, string> Directives(string path)
    {
        var directives = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var space = line.IndexOf(' ');
            var name = space < 0 ? line : line[..space];
            var value = space < 0 ? string.Empty : line[(space + 1)..].Trim();
            directives.TryAdd(name, value).Should().BeTrue($"{path} gives '{name}' once");
        }
        return directives;
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "nx.json")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException($"No repository root (nx.json) above {AppContext.BaseDirectory}.");
    }
}
