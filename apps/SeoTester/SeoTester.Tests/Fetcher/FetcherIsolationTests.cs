using System.Reflection;
using System.Text.Json;
using SeoTester.Fetcher;
using SeoTester.Tests._Infrastructure;

namespace SeoTester.Tests.Fetcher;

/// <summary>
/// The seo-fetcher has no database client (<c>docs/seo_analyzer_PRD.md</c> D54): the network keeps it away from
/// RavenDB, and this keeps the code away too, so the process that parses untrusted websites could not talk to a
/// database even if the network allowed it. Two views of the same rule: the restore graph (every package and project,
/// transitive, used or not) and the compiled references.
/// </summary>
public class FetcherIsolationTests
{
    /// <summary>The only Spark packages the fetcher may carry: none of them reaches RavenDB, the pipeline or mail.</summary>
    private static readonly HashSet<string> AllowedSparkPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "MintPlayer.Spark.SafeHttp",                // the SSRF-safe client itself
        "MintPlayer.Spark.Attributes",              // via SeoTester.Library (TlsFacts, the TLS hello)
        "MintPlayer.Spark.Messaging.Abstractions",  // via SeoTester.Library (message records only)
        "MintPlayer.Spark.SourceGenerators",        // analyzers of SeoTester.Library, never referenced at run time
        "MintPlayer.Spark.LibraryGenerators",
    };

    private static bool Forbidden(string name)
        => name.StartsWith("RavenDB", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("Raven.", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("Sparrow", StringComparison.OrdinalIgnoreCase)
           || name.Equals("MintPlayer.Spark", StringComparison.OrdinalIgnoreCase)
           || (name.StartsWith("MintPlayer.Spark.", StringComparison.OrdinalIgnoreCase) && !AllowedSparkPackages.Contains(name));

    [Fact]
    public void The_fetchers_restore_graph_holds_no_database_client_and_no_Spark_runtime()
    {
        var assets = AssetsFile();
        using var json = JsonDocument.Parse(File.ReadAllText(assets));
        var libraries = json.RootElement.GetProperty("libraries").EnumerateObject().Select(p => p.Name.Split('/')[0]).ToList();

        libraries.Should().Contain("MintPlayer.Spark.SafeHttp", $"read {assets}");
        libraries.Should().Contain("DnsClient");
        libraries.Where(Forbidden).Should().BeEmpty($"the fetcher must not reach a database ({assets})");
    }

    [Fact]
    public void The_fetchers_compiled_references_hold_no_database_client_and_no_Spark_runtime()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<Assembly>([typeof(FetcherHost).Assembly]);
        while (queue.TryDequeue(out var assembly))
        {
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                if (reference.Name is null || !seen.Add(reference.Name))
                    continue;
                try
                {
                    queue.Enqueue(Assembly.Load(reference));
                }
                catch (FileNotFoundException)
                {
                    // A facade the runtime does not ship: its name is recorded all the same.
                }
            }
        }

        seen.Should().Contain("MintPlayer.Spark.SafeHttp");
        seen.Where(Forbidden).Should().BeEmpty();
    }

    /// <summary>The fetcher's <c>project.assets.json</c>: in its <c>obj</c>, or under an <c>--artifacts-path</c> above the test's output.</summary>
    private static string AssetsFile()
    {
        var candidates = new List<string> { Path.Combine(SeoTesterPaths.RepositoryRoot, "apps", "SeoTester", "SeoTester.Fetcher", "obj", "project.assets.json") };
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            candidates.Add(Path.Combine(dir.FullName, "obj", "SeoTester.Fetcher", "project.assets.json"));
        return candidates.Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            ?? throw new InvalidOperationException("No project.assets.json of SeoTester.Fetcher found; looked at: " + string.Join(", ", candidates));
    }
}
