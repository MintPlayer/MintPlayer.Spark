using System.Text;
using CodeCoverage.Onboarding;
using YamlDotNet.RepresentationModel;

namespace CodeCoverage.Formatting;

/// <summary>
/// The formatting workflow a repository runs (#657 D2, D12, D20): the template onboarding installs, the action reference it
/// calls, and what the server reads back from the repository's copy (the <c>exclude</c> globs, D11).
/// </summary>
public static class FormattingWorkflow
{
    /// <summary>The workflow's file name, as the dispatch and the provenance check name it.</summary>
    public const string FileName = "formatting.yml";

    /// <summary>Where onboarding puts it.</summary>
    public const string Path = ".github/workflows/" + FileName;

    public const string ActionPath = "MintPlayer/MintPlayer.Spark/.github/actions/format";

    /// <summary>The moving tag the template calls (D20), moved by <c>format-action-publish.yml</c>.</summary>
    public const string ActionTag = "format-v1";

    /// <summary>The run's report (D20): its tools, versions and notes, quoted in the pull request.</summary>
    public const string ReportArtifactName = "format-report";

    /// <summary>The largest report read.</summary>
    public const long MaxReportBytes = 256 * 1024;

    /// <summary>The modes a person starts (D3); <c>pipeline</c> is the merge queue's and the backports'.</summary>
    public const string FormatMode = "format";
    public const string LintMode = "lint";

    /// <summary>The bot branch a mode's pull request comes from; the git writer's <c>DependencyBranches.FormatOf</c>.</summary>
    public static string Branch(string mode) => $"mintplayer/{mode}";

    /// <summary>The pull request's title.</summary>
    public static string Title(string mode) => mode == LintMode ? "Lint the repository" : "Format the repository";

    /// <summary>
    /// The pull request's body: what ran, at which version, how many files changed, and the run's notes. A report that cannot be
    /// read leaves the run link alone; it is the repository's own output, so every value is shown as code, never as markup.
    /// </summary>
    public static string Body(string mode, string? runUrl, byte[]? report, int files, bool queued)
    {
        var body = new StringBuilder();
        body.Append(mode == LintMode ? "Fixes what the repository's linters can fix" : "Formats the repository with its own formatters")
            .Append(", as its `formatting.yml` workflow ran them").Append(runUrl is { Length: > 0 } ? $" ([run]({runUrl}))" : "").Append(".\n\n");
        body.Append($"{files} file{(files == 1 ? "" : "s")} changed.\n");
        if (Report.Parse(report) is { } parsed)
        {
            if (parsed.Tools.Count > 0)
            {
                body.Append("\n| Tool | Version | Files |\n|-|-|-|\n");
                foreach (var tool in parsed.Tools.Take(20))
                    body.Append($"| {Code(tool.Id)} | {Code(tool.Version ?? "unpinned")} | {tool.Files} |\n");
            }
            if (mode == LintMode && parsed.RemainingProblems > 0)
                body.Append($"\n{parsed.RemainingProblems} problem{(parsed.RemainingProblems == 1 ? "" : "s")} remain that the linters cannot fix themselves.\n");
            foreach (var note in parsed.Notes.Take(10))
                body.Append("\n> ").Append(Plain(note)).Append('\n');
        }
        if (queued)
            body.Append("\nHanded to the merge queue; it still waits for the reviews and checks the repository requires.\n");
        return body.ToString();
    }

    /// <summary>The parts of <c>format-report.json</c> the server uses.</summary>
    public sealed record Report(IReadOnlyList<ReportTool> Tools, IReadOnlyList<string> Notes, int RemainingProblems)
    {
        public static Report? Parse(byte[]? json)
        {
            if (json is not { Length: > 0 } || json.Length > MaxReportBytes) return null;
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
                var tools = new List<ReportTool>();
                if (root.TryGetProperty("tools", out var list) && list.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var tool in list.EnumerateArray())
                        if (tool.ValueKind == System.Text.Json.JsonValueKind.Object && Text(tool, "id") is { } id)
                            tools.Add(new ReportTool(id, Text(tool, "version"),
                                tool.TryGetProperty("files", out var files) && files.TryGetInt32(out var count) ? count : 0));
                var notes = root.TryGetProperty("notes", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.Array
                    ? n.EnumerateArray().Where(e => e.ValueKind == System.Text.Json.JsonValueKind.String).Select(e => e.GetString()!).ToList()
                    : [];
                var remaining = root.TryGetProperty("remainingProblems", out var r) && r.TryGetInt32(out var value) ? value : 0;
                return new Report(tools, notes, remaining);
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }

        private static string? Text(System.Text.Json.JsonElement element, string name)
            => element.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() : null;
    }

    public sealed record ReportTool(string Id, string? Version, int Files);

    /// <summary>A value as inline code: backticks and line breaks cannot escape it, and it is cut at 100 characters.</summary>
    private static string Code(string value)
    {
        var text = new string([.. value.Take(100).Select(c => char.IsControl(c) || c is '`' or '|' ? ' ' : c)]);
        return $"`{text}`";
    }

    /// <summary>A note as plain text: one line, at most 500 characters, with Markdown's active characters escaped.</summary>
    private static string Plain(string value)
    {
        var builder = new StringBuilder();
        foreach (var c in value.Take(500))
        {
            if (char.IsControl(c)) builder.Append(' ');
            else if (c is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>' or '#' or '|' or '!' or '@') builder.Append('\\').Append(c);
            else builder.Append(c);
        }
        return builder.ToString();
    }

    /// <summary>The template, with LF line endings whatever the build machine checked out.</summary>
    public static string Template()
    {
        const string name = "FormattingTemplates.formatting.yml";
        using var stream = typeof(FormattingWorkflow).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The embedded workflow template '{name}' is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().ReplaceLineEndings("\n");
    }

    /// <summary>A repository's copy with a commit pin of the action moved back to the tag (owner, 2026-11-13).</summary>
    public static string AtTag(string workflow) => ActionTagReference.AtTag(workflow, ActionPath, ActionTag);

    /// <summary>
    /// D11: the <c>exclude</c> globs of every step that calls the format action, one per line, comments and blanks dropped.
    /// Empty when the file has none or cannot be read: the runner applies the same list, so the server's copy is a second
    /// fence, not the only one.
    /// </summary>
    public static IReadOnlyList<string> Exclude(string? workflow)
    {
        if (string.IsNullOrWhiteSpace(workflow)) return [];
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(workflow));
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return [];
        }
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root
            || Child(root, "jobs") is not YamlMappingNode jobs)
            return [];

        var globs = new List<string>();
        foreach (var job in jobs.Children.Values.OfType<YamlMappingNode>())
        {
            if (Child(job, "steps") is not YamlSequenceNode steps) continue;
            foreach (var step in steps.Children.OfType<YamlMappingNode>())
            {
                if (Child(step, "uses") is not YamlScalarNode { Value: { } uses }
                    || !uses.StartsWith(ActionPath + "@", StringComparison.Ordinal))
                    continue;
                if (Child(step, "with") is YamlMappingNode with && Child(with, "exclude") is YamlScalarNode { Value: { } exclude })
                    globs.AddRange(ParseList(exclude));
            }
        }
        return [.. globs.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>The same rule as <c>format.mjs</c>'s <c>parseList</c>: trimmed lines, no blanks, no <c>#</c> comments.</summary>
    public static IReadOnlyList<string> ParseList(string text)
        => [.. text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'))];

    private static YamlNode? Child(YamlMappingNode node, string key)
        => node.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;
}
