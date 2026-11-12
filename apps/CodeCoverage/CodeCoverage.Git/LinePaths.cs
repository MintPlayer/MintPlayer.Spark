namespace CodeCoverage.Git;

/// <summary>
/// The files a release line's narrowing may write (#638 D15, #649 D7). The app's <c>LineNarrowing</c> reads and writes these,
/// and the git writer's <c>/jobs/onboarding</c> accepts a narrowing of exactly these: one predicate, so the producer and the
/// check cannot drift apart again (#649: #648 taught the narrowing workflows and the writer still refused them).
/// </summary>
public static class LinePaths
{
    private static readonly string[] MsBuildExtensions = [".csproj", ".fsproj", ".vbproj", ".props", ".targets"];

    private const string WorkflowsDirectory = ".github/workflows/";

    public static bool IsMsBuild(string path) => MsBuildExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    public static bool IsGlobalJson(string path) => path == "global.json" || path.EndsWith("/global.json", StringComparison.Ordinal);

    /// <summary>
    /// A workflow GitHub runs: <c>.github/workflows/&lt;name&gt;.yml</c> or <c>.yaml</c>, directly in that directory. GitHub loads
    /// none from a subdirectory, and the onboarding's own check takes the same set.
    /// </summary>
    public static bool IsWorkflow(string path)
        => path.StartsWith(WorkflowsDirectory, StringComparison.Ordinal)
           && path.IndexOf('/', WorkflowsDirectory.Length) < 0
           && path.Length > WorkflowsDirectory.Length + ".yml".Length
           && (path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase));

    /// <summary>What a narrowing may write: project files, global.json and workflows.</summary>
    public static bool IsNarrowable(string path) => IsMsBuild(path) || IsGlobalJson(path) || IsWorkflow(path);
}
