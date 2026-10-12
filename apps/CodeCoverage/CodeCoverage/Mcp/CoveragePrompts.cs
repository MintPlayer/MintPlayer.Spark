using System.ComponentModel;
using ModelContextProtocol.Server;

namespace CodeCoverage.Mcp;

/// <summary>
/// CodeCoverage's one MCP prompt (MCP PRD §9: one per adopter): "raise coverage of this repository", served as
/// <c>codecoverage_raise_coverage</c>. A message template the user picks in the agent; it reads no data.
/// </summary>
/// <remarks>
/// It names the tools by their served names, so it assumes <c>mcp.ToolPrefix = "codecoverage"</c> (set explicitly in
/// <c>Program.cs</c>); a test checks every name it mentions is listed.
/// </remarks>
[McpServerPromptType]
public sealed class CoveragePrompts
{
    [McpServerPrompt(Name = "raise_coverage")]
    [Description("Find where tests would raise this repository's line coverage most, then write those tests in your local checkout.")]
    public static string RaiseCoverage(
        [Description("The repository as forge/owner/name, e.g. github/octo-org/octo-repo.")] string repository,
        [Description("The branch to improve; the repository's default branch when omitted.")] string? branch = null)
        => $"""
            Raise the test coverage of {repository} on {(string.IsNullOrWhiteSpace(branch) ? "its default branch" : $"the branch {branch}")}, using the CodeCoverage tools and the local checkout of that repository.

            1. Call codecoverage_summary for the current totals and the recent trend.
            2. Call codecoverage_hotspots to list the files with the most uncovered lines. Weigh coveredPercent and uncoveredRanges yourself: a file that already has tests and whose uncovered lines sit in few ranges is often the quickest win, but the server does not rank by that.
            3. For the file you pick, call codecoverage_file and read those line ranges in the local checkout.
            4. Write tests for those lines next to the existing tests, in the project's own test style, and run them.
            5. Report which lines the new tests cover. CodeCoverage shows the effect only after CI uploads coverage for a new commit.
            """;
}
