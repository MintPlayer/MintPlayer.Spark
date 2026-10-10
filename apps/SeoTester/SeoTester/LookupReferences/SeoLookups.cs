using MintPlayer.Spark.Abstractions;

namespace SeoTester.LookupReferences;

// The choice lists of SeoTester's documents. Keys are stored; the labels are en/fr/nl (_TS order: English, French,
// Dutch). The check categories and severities mirror SeoTester.Catalogue's enums, by name.

/// <summary>A site's scheduled monitoring (D12): off, weekly or monthly. Gated by <c>SeoTester:Features:Monitoring</c>.</summary>
public sealed class MonitoringSchedule : TransientLookupReference<string>
{
    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;
    public const string Off = nameof(Off);
    public const string Weekly = nameof(Weekly);
    public const string Monthly = nameof(Monthly);

    private MonitoringSchedule() { }

    public static IReadOnlyCollection<MonitoringSchedule> Items { get; } =
    [
        new MonitoringSchedule { Key = Off, Description = "No scheduled runs", Values = _TS("Off", "Désactivé", "Uit") },
        new MonitoringSchedule { Key = Weekly, Description = "A run every week", Values = _TS("Weekly", "Hebdomadaire", "Wekelijks") },
        new MonitoringSchedule { Key = Monthly, Description = "A run every month", Values = _TS("Monthly", "Mensuel", "Maandelijks") },
    ];
}

/// <summary>How ownership of a site was proven (F3, D29).</summary>
public sealed class VerificationMethod : TransientLookupReference<string>
{
    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;
    public const string Dns = nameof(Dns);
    public const string MetaTag = nameof(MetaTag);
    public const string WellKnownFile = nameof(WellKnownFile);

    private VerificationMethod() { }

    public static IReadOnlyCollection<VerificationMethod> Items { get; } =
    [
        new VerificationMethod { Key = Dns, Description = "A TXT record at _seotester.<host>", Values = _TS("DNS TXT record", "Enregistrement DNS TXT", "DNS TXT-record") },
        new VerificationMethod { Key = MetaTag, Description = "A meta tag on the home page", Values = _TS("Meta tag", "Balise meta", "Metatag") },
        new VerificationMethod { Key = WellKnownFile, Description = "A file at /.well-known/seotester-verification.txt", Values = _TS("File in /.well-known/", "Fichier dans /.well-known/", "Bestand in /.well-known/") },
    ];
}

/// <summary>Where an audit run is.</summary>
public sealed class AuditRunStatus : TransientLookupReference<string>
{
    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;
    public const string Queued = nameof(Queued);
    public const string Running = nameof(Running);
    public const string Completed = nameof(Completed);
    public const string Cancelled = nameof(Cancelled);
    public const string Failed = nameof(Failed);

    /// <summary>A run in one of these can still be joined (D17).</summary>
    public static readonly IReadOnlyList<string> InProgress = [Queued, Running];

    private AuditRunStatus() { }

    public static IReadOnlyCollection<AuditRunStatus> Items { get; } =
    [
        new AuditRunStatus { Key = Queued, Description = "Waiting for the crawler", Values = _TS("Queued", "En attente", "In de wachtrij") },
        new AuditRunStatus { Key = Running, Description = "Being crawled", Values = _TS("Running", "En cours", "Bezig") },
        new AuditRunStatus { Key = Completed, Description = "Finished and scored", Values = _TS("Completed", "Terminé", "Klaar") },
        new AuditRunStatus { Key = Cancelled, Description = "Stopped on request", Values = _TS("Cancelled", "Annulé", "Geannuleerd") },
        new AuditRunStatus { Key = Failed, Description = "Stopped by an error", Values = _TS("Failed", "Échoué", "Mislukt") },
    ];
}

/// <summary>The catalogue's categories (SeoTester.Catalogue.CheckCategory).</summary>
public sealed class CheckCategoryLookup : TransientLookupReference<string>
{
    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;

    private CheckCategoryLookup() { }

    public static IReadOnlyCollection<CheckCategoryLookup> Items { get; } =
    [
        new CheckCategoryLookup { Key = "Seo", Description = "On-page SEO", Values = _TS("SEO", "SEO", "SEO") },
        new CheckCategoryLookup { Key = "Technical", Description = "Technical and crawlability", Values = _TS("Technical", "Technique", "Technisch") },
        new CheckCategoryLookup { Key = "Performance", Description = "Speed and weight", Values = _TS("Performance", "Performance", "Prestaties") },
        new CheckCategoryLookup { Key = "Security", Description = "Transport and headers", Values = _TS("Security", "Sécurité", "Beveiliging") },
        new CheckCategoryLookup { Key = "Accessibility", Description = "Accessibility", Values = _TS("Accessibility", "Accessibilité", "Toegankelijkheid") },
        new CheckCategoryLookup { Key = "Social", Description = "Social previews", Values = _TS("Social", "Réseaux sociaux", "Sociaal") },
        new CheckCategoryLookup { Key = "Mobile", Description = "Mobile and PWA", Values = _TS("Mobile/PWA", "Mobile/PWA", "Mobiel/PWA") },
        new CheckCategoryLookup { Key = "Domain", Description = "Domain and DNS (information)", Values = _TS("Domain/DNS", "Domaine/DNS", "Domein/DNS") },
    ];
}

/// <summary>The catalogue's severities (SeoTester.Catalogue.CheckSeverity).</summary>
public sealed class CheckSeverityLookup : TransientLookupReference<string>
{
    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;

    private CheckSeverityLookup() { }

    public static IReadOnlyCollection<CheckSeverityLookup> Items { get; } =
    [
        new CheckSeverityLookup { Key = "Critical", Description = "Caps its category when it fails", Values = _TS("Critical", "Critique", "Kritiek") },
        new CheckSeverityLookup { Key = "High", Description = "High", Values = _TS("High", "Élevée", "Hoog") },
        new CheckSeverityLookup { Key = "Medium", Description = "Medium", Values = _TS("Medium", "Moyenne", "Gemiddeld") },
        new CheckSeverityLookup { Key = "Low", Description = "Low", Values = _TS("Low", "Faible", "Laag") },
        new CheckSeverityLookup { Key = "Info", Description = "Shown, not scored", Values = _TS("Info", "Info", "Info") },
    ];
}
