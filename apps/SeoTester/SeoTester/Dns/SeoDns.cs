using DnsClient;

namespace SeoTester.Verification;

/// <summary>
/// TXT lookups behind an abstraction (<c>docs/seo_analyzer_PRD.md</c> F3), so tests answer from a table and the crawl's
/// DNS checks (M9) can extend it. What it returns is untrusted: callers compare it exactly and bound what they read.
/// </summary>
public interface IDnsTxtResolver
{
    /// <summary>The TXT records at <paramref name="name"/>, each its strings concatenated; empty when there are none or the lookup failed.</summary>
    Task<IReadOnlyList<string>> GetTxtRecordsAsync(string name, CancellationToken cancellationToken);
}

/// <summary>The system resolvers through DnsClient: short timeouts, one retry, no cache across calls that matter (TTL honoured).</summary>
internal sealed class DnsClientTxtResolver(ILogger<DnsClientTxtResolver> logger) : IDnsTxtResolver
{
    private readonly LookupClient client = new(new LookupClientOptions
    {
        Timeout = TimeSpan.FromSeconds(5),
        Retries = 1,
        UseCache = false,
        ThrowDnsErrors = false,
        ContinueOnDnsError = true,
    });

    public async Task<IReadOnlyList<string>> GetTxtRecordsAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.QueryAsync(name, QueryType.TXT, QueryClass.IN, cancellationToken);
            if (response.HasError)
                return [];
            return response.Answers.TxtRecords().Take(100).Select(r => string.Concat(r.Text)).ToList();
        }
        catch (Exception ex) when (ex is DnsResponseException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("TXT lookup of {Name} failed: {Message}", name, ex.Message);
            return [];
        }
    }
}
