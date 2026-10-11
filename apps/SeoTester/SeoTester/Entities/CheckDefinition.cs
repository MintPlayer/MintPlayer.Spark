using MintPlayer.Spark.Abstractions;
using SeoTester.Catalogue;
using SeoTester.LookupReferences;

namespace SeoTester.Entities;

/// <summary>
/// One check of the catalogue (<c>docs/seo_analyzer_PRD.md</c> §3.1, D16, D32): fully editable by administrators,
/// readable by every signed-in user. Seeded from <c>SeoTester.Library/Catalogue/checks.json</c> by
/// <c>CatalogueSeeder</c>: new checks are added, entries nobody edited follow the shipped text, an administrator's edit
/// is never overwritten, and <c>ResetCheckDefinition</c> restores one entry. The C# implementation is keyed by
/// <see cref="CheckId"/>; an entry without one shows as unavailable (M9).
/// </summary>
public class CheckDefinition
{
    /// <summary><c>CheckDefinitions/{CheckId}</c>.</summary>
    public string? Id { get; set; }

    /// <summary><c>{category}.{kebab-case}</c>, permanent.</summary>
    public string CheckId { get; set; } = string.Empty;

    [LookupReference(typeof(CheckCategoryLookup))]
    public string Category { get; set; } = nameof(Catalogue.CheckCategory.Seo);

    [LookupReference(typeof(CheckSeverityLookup))]
    public string Severity { get; set; } = nameof(CheckSeverity.Medium);

    /// <summary>The weight inside its category (Critical 10, High 6, Medium 3, Low 1, Info 0 as shipped).</summary>
    public int Weight { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Where it is measured (H, R, N, T, D, X, S).</summary>
    public string? Layer { get; set; }

    public string TitleEn { get; set; } = string.Empty;
    public string TitleNl { get; set; } = string.Empty;

    /// <summary>The plain-language message, with <c>{url}</c>, <c>{n}</c>, … placeholders.</summary>
    public string? SummaryEn { get; set; }
    public string? SummaryNl { get; set; }

    /// <summary>The technical explanation behind "How to fix".</summary>
    public string? HowToFixEn { get; set; }
    public string? HowToFixNl { get; set; }

    // Per-platform snippets (D16). Code does not translate.
    public string? SnippetNginx { get; set; }
    public string? SnippetApache { get; set; }
    public string? SnippetIis { get; set; }
    public string? SnippetHtml { get; set; }

    public string? DocsUrl { get; set; }

    /// <summary>
    /// The content hash of what the seeder last wrote (<see cref="ShippedCheck.ContentHash"/>). Equal to the current
    /// content's hash: nobody edited it, so a new shipped text replaces it. Different: an administrator's edit, kept.
    /// Null for an entry an administrator created.
    /// </summary>
    [IgnoreProperty]
    public string? SeededHash { get; set; }

    public static string IdFor(string checkId) => "CheckDefinitions/" + checkId;
}
