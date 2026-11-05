using System.Text;
using System.Text.RegularExpressions;
using CodeCoverage.Forge;

namespace CodeCoverage.PackageVersioning;

/// <summary>
/// Which lines a pull request into the default branch should be backported to (#570 M8b, PRD D5): the supported lines
/// (<see cref="SupportedLines"/>) of the ecosystems it touches. They become <c>backport: &lt;line&gt;</c> labels when it opens,
/// which the maintainer removes where the change should not go, and the informational "Backport targets" check.
/// </summary>
public static partial class BackportTargets
{
    public const string CheckName = "Backport targets";
    public const string LabelPrefix = "backport: ";

    public static string Label(LineSupport line) => LabelPrefix + line.Line;

    /// <summary>
    /// The labels to add. <c>all</c>: every applicable line. <c>fixes</c>: the current line always (D16, a change ships only
    /// once it reaches a line), older lines only for a fix. <c>off</c>: none.
    /// </summary>
    public static IReadOnlyList<string> Labels(IReadOnlyList<LineSupport> lines, IReadOnlyCollection<string> ecosystems, string mode, bool isFix)
        => mode == BackportYml.Off ? [] : [.. lines
            .Where(l => Applies(l, ecosystems) && (mode == BackportYml.All || l.Current || isFix))
            .Select(Label)];

    /// <summary>A conventional-commit fix title (<c>fix:</c>, <c>fix(scope):</c>, <c>fix!:</c>) or one of <c>backport.fixLabels</c>.</summary>
    public static bool LooksLikeFix(string? title, IEnumerable<string> labels, IReadOnlyList<string> fixLabels)
        => title is not null && FixTitle().IsMatch(title) || labels.Any(l => fixLabels.Contains(l, StringComparer.OrdinalIgnoreCase));

    /// <summary>The check: every existing line, whether it is supported, touched and labelled. Neutral: it never blocks.</summary>
    public static ForgeVersioningCheck Check(IReadOnlyList<LineSupport> lines, IReadOnlyCollection<string> ecosystems, IReadOnlyCollection<string> labels,
        IReadOnlyList<string> errors)
    {
        var applying = lines.Where(l => Applies(l, ecosystems)).Select(l => l.Line.ToString()).ToList();
        var title = applying.Count switch
        {
            0 => "No line applies",
            1 => $"1 line applies: {applying[0]}",
            _ => $"{applying.Count} lines apply: {string.Join(", ", applying)}",
        };
        var summary = ecosystems.Count == 0
            ? "This pull request changes no package, so no release line needs it."
            : $"This pull request changes {string.Join(" and ", ecosystems)} packages. A line applies when it is supported and its ecosystem is changed. "
                + "Merging backports it to each line labelled `backport: <line>`; remove a label to leave a line out.";

        var text = new StringBuilder("| Line | Branch | Supported | Changed | Labelled |\n|---|---|---|---|---|\n");
        foreach (var line in lines)
        {
            var supported = line.Supported ? line.Current ? "yes, the current line" : "yes" : $"no: {line.Reason}";
            text.Append($"| `{line.Line}` | `{line.Branch}` | {supported} | {YesNo(ecosystems.Contains(line.Line.Ecosystem))} | {YesNo(labels.Contains(Label(line)))} |\n");
        }
        if (lines.Count == 0)
            text.Append("| | no release line exists yet | | | |\n");
        if (errors.Count > 0)
            text.Append("\nThe `backport:` section of coverage.yml has errors:\n").Append(string.Join('\n', errors.Take(10).Select(e => $"- {e}"))).Append('\n');

        return new ForgeVersioningCheck(CheckName, true, "neutral", title, summary, text.ToString());
    }

    private static bool Applies(LineSupport line, IReadOnlyCollection<string> ecosystems) => line.Supported && ecosystems.Contains(line.Line.Ecosystem);

    private static string YesNo(bool value) => value ? "yes" : "no";

    [GeneratedRegex(@"\Afix(\([^)]*\))?!?:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FixTitle();
}
