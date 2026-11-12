using System.Text;
using CodeCoverage.Git;
using CodeCoverage.Versioning.Releases;
using YamlDotNet.RepresentationModel;

namespace CodeCoverage.Onboarding;

/// <summary>
/// The workflow triggers release lines need (the Tools onboarding's gap, 2026-11-12): with lines on, packages publish only from
/// a line, so the push workflow that runs the versioning step must run on the lines too, and the pull-request workflows
/// must run for pull requests into them (the backports' CI, and the checks ruleset 3 requires). A trigger without a branch
/// filter runs everywhere already; a filter that names the default branch but no line gets the lines' glob. Every other
/// push workflow is left alone: a deploy that runs on the default branch only must not run on a line.
/// </summary>
/// <remarks>
/// The edit is text, never a re-serialisation, so the owner's comments and layout stay: the glob joins a flow list before
/// its <c>]</c>, or a block list as a new item under its last one. Planning again finds the glob and changes nothing.
/// </remarks>
internal static class ReleaseLineTriggers
{
    /// <summary>
    /// <paramref name="text"/> with the lines' glob added to the branch filters that need it, or null when none does.
    /// <paramref name="push"/>: whether this is a workflow whose <c>push</c> filter should take the lines (a versioning
    /// workflow); every workflow's <c>pull_request</c> filter does.
    /// </summary>
    public static string? Add(string path, string text, ReleaseLinePattern pattern, string defaultBranch, bool push, List<string> notes)
    {
        YamlMappingNode? root;
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(text));
            root = stream.Documents.FirstOrDefault()?.RootNode as YamlMappingNode;
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return null;
        }
        if (root is null || Child(root, "on") is not YamlMappingNode on) return null;

        var glob = Glob(pattern);
        var line = pattern.Format(new ReleaseLine(Ecosystems.Dotnet, 1));
        var inserts = new List<(int Offset, string Text)>();
        foreach (var name in push ? new[] { "push", "pull_request" } : ["pull_request"])
        {
            if (Child(on, name) is not YamlMappingNode trigger) continue;
            if (Child(trigger, "branches-ignore") is YamlSequenceNode ignored && Matches(ignored, line))
            {
                notes.Add($"`{path}` ignores the release lines in its `{name}` trigger (`branches-ignore`): let it run on `{glob}`, or the lines get no {(name == "push" ? "release" : "checks")}.");
                continue;
            }
            if (Child(trigger, "branches") is not YamlSequenceNode branches || Matches(branches, line)) continue;
            if (!Matches(branches, defaultBranch)) continue;
            if (branches.Children.Any(b => b is YamlScalarNode { Value: { } v } && v.StartsWith('!')))
            {
                notes.Add($"`{path}` has a negated branch in its `{name}` trigger: add `{glob}` to it by hand so it runs on the release lines.");
                continue;
            }
            if (Insert(text, branches, glob) is { } insert) inserts.Add(insert);
        }
        if (inserts.Count == 0) return null;

        var edited = new StringBuilder(text);
        foreach (var (offset, insertion) in inserts.OrderByDescending(i => i.Offset))
            edited.Insert(offset, insertion);
        return edited.ToString();
    }

    /// <summary>The lines as a GitHub branch filter glob: <c>release/*-*.x</c> for the default pattern.</summary>
    public static string Glob(ReleaseLinePattern pattern) => pattern.RefGlob["refs/heads/".Length..];

    private static (int Offset, string Text)? Insert(string text, YamlSequenceNode branches, string glob)
    {
        var item = $"'{glob}'";
        if (branches.Style == YamlDotNet.Core.Events.SequenceStyle.Flow)
        {
            // YamlDotNet leaves a flow sequence's End at its Start, so the `]` is the first one after the last item (an item's
            // own `]` sits inside its quotes, before its End).
            var after = branches.Children.Count == 0 ? (int)branches.Start.Index + 1 : (int)branches.Children[^1].End.Index;
            var close = text.IndexOf(']', after);
            if (close < 0) return null;
            var before = text[(int)branches.Start.Index..close].TrimEnd();
            // After the last item, before any padding: `[ master ]` becomes `[ master, 'release/*-*.x' ]`.
            return ((int)branches.Start.Index + before.Length, (before.EndsWith('[') ? "" : ", ") + item);
        }
        if (branches.Children.Count == 0) return null;

        var last = branches.Children[^1];
        var lineStart = text.LastIndexOf('\n', (int)last.Start.Index) + 1;
        var dash = text.IndexOf('-', lineStart);
        if (dash < 0 || dash > last.Start.Index) return null;
        var lineEnd = text.IndexOf('\n', (int)last.Start.Index);
        var newline = lineEnd > 0 && text[lineEnd - 1] == '\r' ? "\r\n" : "\n";
        var entry = new string(' ', dash - lineStart) + "- " + item;
        return lineEnd < 0 ? (text.Length, newline + entry) : (lineEnd + 1, entry + newline);
    }

    private static bool Matches(YamlSequenceNode filter, string branch)
        => filter.Children.OfType<YamlScalarNode>().Any(b => b.Value is { Length: > 0 } v && !v.StartsWith('!') && new PathGlob(v).Matches(branch));

    private static YamlNode? Child(YamlMappingNode node, string key)
        => node.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;
}
