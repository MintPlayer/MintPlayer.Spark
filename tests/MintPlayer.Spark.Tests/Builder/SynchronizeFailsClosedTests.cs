using Microsoft.Extensions.DependencyInjection;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Builder;

/// <summary>
/// Spike S7 of <c>docs/coverage_merge_queue_PRD.md</c>: <c>--spark-synchronize-model</c> fails closed on a
/// model file it cannot read. It exits 1, names the file, and writes nothing.
/// </summary>
/// <remarks>
/// Red before the change: the load error was caught and only printed ("Error loading model file …"), the
/// entity was then regenerated as if it were new, with fresh GUIDs, and the file was overwritten. The hand
/// edits and every id other files referenced were gone, and the command exited 0. A rebase that leaves git
/// conflict markers in <c>Model/*.json</c> is the motivating case.
/// </remarks>
[Collection(ProcessExitCodeCollection.Name)]
public class SynchronizeFailsClosedTests
{
    public sealed class FcProbe
    {
        public string? Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public sealed class FcContext : SparkContext
    {
        public IRavenQueryable<FcProbe> Probes => Session.Query<FcProbe>();
    }

    [Fact]
    public void A_model_file_with_conflict_markers_exits_1_and_leaves_App_Data_untouched()
    {
        using var scratch = new ScratchContentRoot("spark-sync-failclosed-");
        scratch.Synchronize<FcContext>();
        Environment.ExitCode.Should().Be(0);

        var modelFile = Path.Combine(scratch.ModelPath, "FcProbe.json");
        var lines = File.ReadAllLines(modelFile).ToList();
        var nameLine = lines.FindIndex(l => l.Contains("\"name\": \"FcProbe\""));
        nameLine.Should().BeGreaterThan(0);
        lines.InsertRange(nameLine, ["<<<<<<< HEAD", lines[nameLine], "======="]);
        lines.Insert(nameLine + 4, ">>>>>>> feature");
        File.WriteAllLines(modelFile, lines);
        var before = Snapshot(scratch.Path);

        var (exitCode, reported) = Run(scratch);

        exitCode.Should().Be(1);
        reported.Should().Contain("FcProbe.json");
        // A file that cannot be read must stop the run before anything is regenerated.
        Snapshot(scratch.Path).Should().Equal(before);
    }

    private static (int ExitCode, string Reported) Run(ScratchContentRoot scratch)
    {
        Environment.ExitCode = 0;
        var captured = new StringWriter();
        var previous = Console.Error;
        Console.SetError(captured);
        try
        {
            var builder = scratch.CreateBuilder();
            builder.Services.AddScoped<SparkContext, FcContext>();
            builder.SynchronizeSparkModelsIfRequested(["--spark-synchronize-model"]);
        }
        finally
        {
            Console.SetError(previous);
        }
        return (Environment.ExitCode, captured.ToString());
    }

    private static (string Path, string Content)[] Snapshot(string root) =>
        Directory.GetFiles(Path.Combine(root, "App_Data"), "*", SearchOption.AllDirectories)
            .Select(f => (Path: Path.GetRelativePath(root, f), Content: File.ReadAllText(f)))
            .OrderBy(x => x.Path, StringComparer.Ordinal)
            .ToArray();
}
