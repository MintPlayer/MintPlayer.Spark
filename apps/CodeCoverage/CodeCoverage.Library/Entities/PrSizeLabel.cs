using System.Drawing;
using CodeCoverage.LookupReferences;
using MintPlayer.Spark.Abstractions;

namespace CodeCoverage.Entities;

/// <summary>
/// One row of an account's PR size scale (docs/coverage_pr_size_labels_PRD.md D2), embedded in
/// <see cref="Account.PrSizeLabelScale"/>: a pull request whose size is at least <see cref="MinLines"/>, and below the next
/// row's, gets the label <see cref="Name"/>.
/// </summary>
/// <remarks>
/// The <see cref="Id"/> is what the app tracks a label by (D3), so renaming a row renames the label on GitHub instead of
/// replacing it, and the label stays on every pull request that already carries it.
/// </remarks>
[ValueObject]
public partial class PrSizeLabel
{
    /// <summary>Stable key of the row, so a save populates onto the stored row, and a rename keeps the label (D3).</summary>
    [ValueKey]
    public string Id { get; set; } = string.Empty;

    /// <summary>The label's name on the forge, <c>size/M</c>. Unique per scale, case-insensitively, at most 50 characters.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The smallest size, in changed lines, that gets this label. The smallest row of a scale is 0.</summary>
    public int MinLines { get; set; }

    /// <summary>The label's colour. The alpha channel is ignored: GitHub takes six hex digits.</summary>
    public Color Color { get; set; }

    /// <summary>The label's description; empty generates "Denotes a PR that changes 100-299 lines" from the scale.</summary>
    public string? Description { get; set; }
}

/// <summary>
/// One ignore rule (PRD D5), embedded in <see cref="Account.PrSizeIgnoreRules"/> and <see cref="Repository.PrSizeIgnoreRules"/>.
/// The last rule whose glob matches a path decides how the file counts; a glob starting with <c>!</c> counts it fully and
/// ignores <see cref="Mode"/>.
/// </summary>
[ValueObject]
public partial class PrSizeIgnoreRule
{
    /// <summary>Stable key of the row, so a save populates onto the stored row.</summary>
    [ValueKey]
    public string Id { get; set; } = string.Empty;

    /// <summary>A repository-relative path glob (<c>**/*.md</c>); <c>**</c> spans directories, matching is case-sensitive.</summary>
    public string Glob { get; set; } = string.Empty;

    /// <summary>How a matching file counts.</summary>
    [LookupReference(typeof(PrSizeIgnoreMode))]
    public EPrSizeIgnoreMode Mode { get; set; }
}

/// <summary>
/// What the app last wrote to the forge for one scale row (PRD D3), kept in <see cref="Repository.PrSizeLabelState"/>.
/// Internal state: never shown, never edited.
/// </summary>
[ValueObject]
public partial class ManagedLabel
{
    /// <summary>The <see cref="PrSizeLabel.Id"/> this label was written for.</summary>
    [ValueKey]
    public string RowId { get; set; } = string.Empty;

    /// <summary>The name the label has on the forge, which is the name to PATCH or delete by.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The colour last written, six lower-case hex digits without <c>#</c>.</summary>
    public string Color { get; set; } = string.Empty;

    /// <summary>The description last written.</summary>
    public string Description { get; set; } = string.Empty;
}

/// <summary>The defaults a new account starts with (PRD D2, D5): pascalgn/size-label-action's scale and the reference's ignore rules.</summary>
/// <remarks>
/// Factories rather than property initializers: a non-empty list initializer on a deserialized entity risks the stored
/// rows being appended to the defaults instead of replacing them. Every place that creates an <see cref="Account"/> calls
/// <see cref="Account.ApplyPrSizeDefaults"/>, and the migration backfills the stored ones.
/// </remarks>
public static class PrSizeDefaults
{
    public static List<PrSizeLabel> Scale() =>
    [
        new() { Id = "xs", Name = "size/XS", MinLines = 0, Color = ColorTranslator.FromHtml("#009900") },
        new() { Id = "s", Name = "size/S", MinLines = 10, Color = ColorTranslator.FromHtml("#77bb00") },
        new() { Id = "m", Name = "size/M", MinLines = 30, Color = ColorTranslator.FromHtml("#eebb00") },
        new() { Id = "l", Name = "size/L", MinLines = 100, Color = ColorTranslator.FromHtml("#ee9900") },
        new() { Id = "xl", Name = "size/XL", MinLines = 500, Color = ColorTranslator.FromHtml("#ee5500") },
        new() { Id = "xxl", Name = "size/XXL", MinLines = 1000, Color = ColorTranslator.FromHtml("#ee0000") },
    ];

    /// <remarks>
    /// The ignore-additions rules come first: the last matching rule wins, so <c>**/package-lock.json</c> must follow
    /// <c>**/*.json</c> to be ignored outright, as the reference ignores it.
    /// </remarks>
    public static List<PrSizeIgnoreRule> IgnoreRules() =>
    [
        new() { Id = "json", Glob = "**/*.json", Mode = EPrSizeIgnoreMode.IgnoreAdditions },
        new() { Id = "tests", Glob = "**/*Tests/**", Mode = EPrSizeIgnoreMode.IgnoreAdditions },
        new() { Id = "package-lock", Glob = "**/package-lock.json", Mode = EPrSizeIgnoreMode.Ignore },
        new() { Id = "packages-lock", Glob = "**/packages.lock.json", Mode = EPrSizeIgnoreMode.Ignore },
        new() { Id = "directory-packages", Glob = "**/Directory.Packages.props", Mode = EPrSizeIgnoreMode.Ignore },
        new() { Id = "markdown", Glob = "**/*.md", Mode = EPrSizeIgnoreMode.Ignore },
    ];
}
