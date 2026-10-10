namespace SeoTester.Legal;

/// <summary>
/// Who operates SeoTester, for the privacy policy and the terms (<c>docs/seo_analyzer_PRD.md</c> D47), bound from
/// <c>SeoTester:Legal</c>. Configuration only, never the repository: in production the server's <c>.env</c>
/// (<c>LEGAL_*</c>, docs/seotester/deploy.md). An empty value shows as "[to be completed]" on the pages, and production
/// logs a warning at startup (<see cref="LegalConfigurationCheck"/>); it never stops the app.
/// </summary>
public sealed class SeoLegalOptions
{
    public const string SectionName = "SeoTester:Legal";

    /// <summary>The controller's legal name (a person or a company).</summary>
    public string? OperatorName { get; set; }

    /// <summary>The controller's postal address, one line.</summary>
    public string? Address { get; set; }

    /// <summary>The address for questions about the service and for privacy requests.</summary>
    public string? ContactEmail { get; set; }

    /// <summary>The Belgian enterprise number (KBO/BCE), when there is one.</summary>
    public string? EnterpriseNumber { get; set; }

    /// <summary>The courts competent for disputes, e.g. "the courts of …".</summary>
    public string? Court { get; set; }

    /// <summary>Who hosts the server (and where), as the privacy policy names it.</summary>
    public string? HostingProvider { get; set; }

    /// <summary>The configuration keys whose value is empty.</summary>
    public IReadOnlyList<string> Missing()
    {
        var missing = new List<string>();
        void Check(string? value, string name) { if (string.IsNullOrWhiteSpace(value)) missing.Add($"{SectionName}:{name}"); }
        Check(OperatorName, nameof(OperatorName));
        Check(Address, nameof(Address));
        Check(ContactEmail, nameof(ContactEmail));
        Check(EnterpriseNumber, nameof(EnterpriseNumber));
        Check(Court, nameof(Court));
        Check(HostingProvider, nameof(HostingProvider));
        return missing;
    }
}

/// <summary>Logs, in production, which <see cref="SeoLegalOptions"/> values the legal pages still show as "to be completed". A warning, never a failure.</summary>
internal sealed class LegalConfigurationCheck(
    Microsoft.Extensions.Options.IOptions<SeoLegalOptions> options,
    IHostEnvironment environment,
    ILogger<LegalConfigurationCheck> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (environment.IsProduction() && options.Value.Missing() is { Count: > 0 } missing)
            logger.LogWarning("The privacy policy and terms show \"[to be completed]\" for {Keys}: set them in the server's .env (docs/seotester/deploy.md §4.3).",
                string.Join(", ", missing));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
