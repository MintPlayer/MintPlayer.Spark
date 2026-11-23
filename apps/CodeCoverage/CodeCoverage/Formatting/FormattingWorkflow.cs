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
