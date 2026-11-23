using System.Text;
using System.Text.RegularExpressions;
using CodeCoverage.Versioning.Graph;

namespace CodeCoverage.Onboarding;

/// <summary>
/// #693 M12: what onboarding sets up for the changelog. It writes a "Type of change" section into the pull-request
/// template (D12), unless the template policy is off (D13), and it lists the packages that don't name their repository,
/// since Renovate and Dependabot find the Releases through it (D7). Idempotent: a template that has the section already is
/// left as it is, so a rebuilt onboarding pull request changes nothing and a later edit by the owner stays.
/// </summary>
internal static partial class ChangelogOnboarding
{
    /// <summary>Where onboarding creates the template when the repository has none.</summary>
    public const string TemplatePath = ".github/pull_request_template.md";

    /// <summary>The places GitHub reads a single pull-request template from, in its order; the file name ignores case.</summary>
    private static readonly string[] TemplateFolders = [".github/", "", "docs/"];

    /// <summary>
    /// The section. Each box starts with a word <c>PrTitleBuilder.TemplateWords</c> maps to a type, so a ticked box types the
    /// pull request's title and changelog entry. The test <c>Every_box_maps_to_a_type</c> holds that.
    /// </summary>
    public const string Section = """
        ## Type of change

        <!-- Tick one: it types this pull request's title and its changelog entry. Leave them all empty and the title check asks. For a change users never see, add the `changelog: skip` label. -->
        - [ ] Feature
        - [ ] Fix
        - [ ] Performance
        - [ ] Documentation
        - [ ] Refactor
        - [ ] Chore

        """;

    /// <summary>The section's box labels, for the mapping test.</summary>
    public static IEnumerable<string> Boxes => Box().Matches(Section).Select(m => m.Groups["text"].Value);

    /// <param name="template">Whether the template policy is on (<c>Repository.ResolvePrTemplate</c>).</param>
    /// <returns>The pull request's "Changelog" section text.</returns>
    public static async Task<string> PlanAsync(OverlayTree overlay, bool template, List<string> notes, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        text.AppendLine("The changelog is on for this repository: each release's notes are built from the pull requests merged since the last release, "
            + "and pull-request titles are corrected to `<type>: <title>` so every entry has a type. A `## Changelog` section in a pull request's body overrides its entry; "
            + "the `changelog: skip` label leaves it out.");

        if (template)
        {
            var path = TemplateOf(overlay);
            var existing = path is null ? null : await overlay.ReadTextAsync(path, Limits.MaxManifestBytes, cancellationToken);
            if (existing is not null && TypeOfChangeHeading().IsMatch(existing))
                text.AppendLine().AppendLine($"`{path}` has a \"Type of change\" section already; the bot left it as it is.");
            else
            {
                path ??= TemplatePath;
                overlay.Write(path, existing is null ? Section : existing.TrimEnd() + "\n\n" + Section);
                text.AppendLine().AppendLine(existing is null
                    ? $"`{path}` is new: a \"Type of change\" section whose ticked box types the pull request. Edit it as you like; the bot does not touch it again."
                    : $"`{path}` gets a \"Type of change\" section at the end, whose ticked box types the pull request. Move or reword it as you like, as long as each box starts with its type's word; the bot does not touch it again.");
            }
        }

        var graph = await ProjectGraphBuilder.BuildAsync(overlay, cancellationToken: cancellationToken);
        var unnamed = graph.Packages.Where(p => !p.NamesRepository).OrderBy(p => p.Path, StringComparer.Ordinal).ToList();
        if (unnamed.Count > 0)
            notes.Add($"Name the repository in {(unnamed.Count == 1 ? "this package" : $"these {unnamed.Count} packages")}, so Renovate and Dependabot find the release notes: "
                + string.Join(", ", unnamed.Take(20).Select(p => $"`{p.Path}`")) + (unnamed.Count > 20 ? $" and {unnamed.Count - 20} more" : "")
                + ". Set `<RepositoryUrl>` (once, in `Directory.Build.props`, covers every project below it) or `repository` in `package.json`.");
        return text.ToString();
    }

    /// <summary>The repository's single pull-request template, if it has one (a <c>PULL_REQUEST_TEMPLATE/</c> folder is not read).</summary>
    private static string? TemplateOf(OverlayTree overlay)
    {
        foreach (var folder in TemplateFolders)
            foreach (var (path, entry) in overlay.Entries)
                if (entry.IsFile && path.Length == folder.Length + "pull_request_template.md".Length
                    && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
                    && path.EndsWith("pull_request_template.md", StringComparison.OrdinalIgnoreCase))
                    return path;
        return null;
    }

    [GeneratedRegex(@"(?im)^\s*#{1,6}\s*type of change\b")]
    private static partial Regex TypeOfChangeHeading();

    [GeneratedRegex(@"(?m)^- \[ \] (?<text>.+)$")]
    private static partial Regex Box();
}
