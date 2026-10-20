using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MintPlayer.Spark.Commerce;

/// <summary>
/// The startup warnings that need the database (§4.2): a channel whose required platform legal documents are not
/// published (GA2), and no accessibility statement (GA10: Directive (EU) 2019/882 Annex V, configured URL or a
/// published <see cref="LegalDocumentKinds.AccessibilityStatement"/>). Runs once after the host started, off the
/// startup path, and never fails the app.
/// </summary>
internal sealed class CommerceStartupWarnings(
    IOptions<SparkCommerceOptions> options,
    ISparkLegalDocuments legal,
    IHostApplicationLifetime lifetime,
    ILogger<CommerceStartupWarnings> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        lifetime.ApplicationStarted.Register(() => _ = Task.Run(() => CheckAsync(lifetime.ApplicationStopping)));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal async Task CheckAsync(CancellationToken ct)
    {
        try
        {
            foreach (var warning in await EvaluateAsync(options.Value, legal, ct))
                logger.LogWarning("Commerce configuration: {Warning}", warning);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Commerce could not check its legal documents at startup.");
        }
    }

    internal static async Task<IReadOnlyList<string>> EvaluateAsync(SparkCommerceOptions o, ISparkLegalDocuments legal, CancellationToken ct)
    {
        var warnings = new List<string>();
        foreach (var (name, channel) in o.Channels)
        {
            // In connected mode sellers publish their own; the platform's are the fallback, so only Platform mode warns.
            if (o.Merchant != MerchantMode.Platform) break;
            var missing = await legal.GetMissingAsync(null, channel.RequiredLegalDocuments, ct);
            if (missing.Count > 0)
                warnings.Add($"Channel '{name}' cannot open a sale: no published {string.Join(", ", missing)} (GA2).");
        }
        if (string.IsNullOrWhiteSpace(o.Legal.AccessibilityStatementUrl)
            && (await legal.GetMissingAsync(null, [LegalDocumentKinds.AccessibilityStatement], ct)).Count > 0)
            warnings.Add("No accessibility statement: set Spark:Commerce:Legal:AccessibilityStatementUrl or publish an "
                + "'accessibility-statement' legal document (European Accessibility Act, Directive (EU) 2019/882 Annex V) (GA10).");
        return warnings;
    }
}
