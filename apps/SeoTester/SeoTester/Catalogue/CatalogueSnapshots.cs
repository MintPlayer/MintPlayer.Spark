using MintPlayer.Spark.Settings;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using SeoTester.Entities;
using SeoTester.Scoring;
using SeoTester.Settings;

namespace SeoTester.Catalogue;

/// <summary>
/// Builds the scoring snapshot a run is made with (<c>docs/seo_analyzer_PRD.md</c> §7, D32): the seven category
/// weights from the settings and every enabled <see cref="CheckDefinition"/> as stored now. Domain has no weight
/// (an information panel).
/// </summary>
public sealed class CatalogueSnapshots(ISparkSettings settings)
{
    public async Task<ScoringSnapshot> CreateAsync(IAsyncDocumentSession session, CancellationToken cancellationToken)
    {
        var weights = new Dictionary<CheckCategory, int>
        {
            [CheckCategory.Seo] = await settings.GetAsync(SeoSettings.WeightSeo, cancellationToken),
            [CheckCategory.Technical] = await settings.GetAsync(SeoSettings.WeightTechnical, cancellationToken),
            [CheckCategory.Performance] = await settings.GetAsync(SeoSettings.WeightPerformance, cancellationToken),
            [CheckCategory.Security] = await settings.GetAsync(SeoSettings.WeightSecurity, cancellationToken),
            [CheckCategory.Accessibility] = await settings.GetAsync(SeoSettings.WeightAccessibility, cancellationToken),
            [CheckCategory.Social] = await settings.GetAsync(SeoSettings.WeightSocial, cancellationToken),
            [CheckCategory.Mobile] = await settings.GetAsync(SeoSettings.WeightMobile, cancellationToken),
        };

        var checks = new List<ScoredCheck>();
        await using (var stream = await session.Advanced.StreamAsync(session.Query<CheckDefinition>(), cancellationToken))
        {
            while (await stream.MoveNextAsync())
            {
                var definition = stream.Current.Document;
                if (CheckDefinitionMapping.ToScored(definition) is { } scored)
                    checks.Add(scored);
            }
        }
        return ScoringSnapshot.Create(weights, checks);
    }
}

/// <summary>Between the stored <see cref="CheckDefinition"/> and the Library's pure shapes.</summary>
public static class CheckDefinitionMapping
{
    /// <summary>The definition as the scoring engine sees it; null when its category or severity is not a known value.</summary>
    public static ScoredCheck? ToScored(CheckDefinition definition)
        => Enum.TryParse<CheckCategory>(definition.Category, out var category) && Enum.IsDefined(category)
           && Enum.TryParse<CheckSeverity>(definition.Severity, out var severity) && Enum.IsDefined(severity)
           && !string.IsNullOrEmpty(definition.CheckId)
            ? new ScoredCheck { CheckId = definition.CheckId, Category = category, Severity = severity, Weight = definition.Weight, Enabled = definition.Enabled }
            : null;

    /// <summary>The definition's content in the shipped shape, to hash it like the seeder did; null for unknown enum values.</summary>
    public static ShippedCheck? ToShipped(CheckDefinition definition)
    {
        if (!Enum.TryParse<CheckCategory>(definition.Category, out var category) || !Enum.TryParse<CheckSeverity>(definition.Severity, out var severity))
            return null;
        var snippets = new Dictionary<string, string>();
        void Snippet(string platform, string? code) { if (!string.IsNullOrEmpty(code)) snippets[platform] = code; }
        Snippet("nginx", definition.SnippetNginx);
        Snippet("apache", definition.SnippetApache);
        Snippet("iis", definition.SnippetIis);
        Snippet("html", definition.SnippetHtml);
        return new ShippedCheck
        {
            Id = definition.CheckId,
            Category = category,
            Severity = severity,
            Weight = definition.Weight,
            Layer = definition.Layer ?? string.Empty,
            Enabled = definition.Enabled,
            Title = new LocalizedText { En = definition.TitleEn, Nl = definition.TitleNl },
            Summary = new LocalizedText { En = definition.SummaryEn ?? string.Empty, Nl = definition.SummaryNl ?? string.Empty },
            HowToFix = new LocalizedText { En = definition.HowToFixEn ?? string.Empty, Nl = definition.HowToFixNl ?? string.Empty },
            Snippets = snippets,
            DocsUrl = string.IsNullOrEmpty(definition.DocsUrl) ? null : definition.DocsUrl,
        };
    }

    /// <summary>Writes <paramref name="shipped"/> onto <paramref name="definition"/> and records its hash as seeded.</summary>
    public static void Apply(ShippedCheck shipped, CheckDefinition definition)
    {
        definition.Id = CheckDefinition.IdFor(shipped.Id);
        definition.CheckId = shipped.Id;
        definition.Category = shipped.Category.ToString();
        definition.Severity = shipped.Severity.ToString();
        definition.Weight = shipped.Weight;
        definition.Layer = string.IsNullOrEmpty(shipped.Layer) ? null : shipped.Layer;
        definition.Enabled = shipped.Enabled;
        definition.TitleEn = shipped.Title.En;
        definition.TitleNl = shipped.Title.Nl;
        definition.SummaryEn = NullIfEmpty(shipped.Summary.En);
        definition.SummaryNl = NullIfEmpty(shipped.Summary.Nl);
        definition.HowToFixEn = NullIfEmpty(shipped.HowToFix.En);
        definition.HowToFixNl = NullIfEmpty(shipped.HowToFix.Nl);
        definition.SnippetNginx = shipped.Snippets.GetValueOrDefault("nginx");
        definition.SnippetApache = shipped.Snippets.GetValueOrDefault("apache");
        definition.SnippetIis = shipped.Snippets.GetValueOrDefault("iis");
        definition.SnippetHtml = shipped.Snippets.GetValueOrDefault("html");
        definition.DocsUrl = shipped.DocsUrl;
        // Hash what was written, read back the way the seeder will read it next time (empty and null alike).
        definition.SeededHash = ToShipped(definition)!.ContentHash();
    }

    /// <summary>Nobody changed it since the seeder (or a reset) wrote it.</summary>
    public static bool IsUnedited(CheckDefinition definition)
        => definition.SeededHash is { Length: > 0 } seeded && ToShipped(definition)?.ContentHash() == seeded;

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
