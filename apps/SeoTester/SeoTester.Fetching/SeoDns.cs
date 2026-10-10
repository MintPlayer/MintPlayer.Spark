using DnsClient;
using DnsClient.Protocol;

namespace SeoTester.Dns;

/// <summary>The record types SeoTester asks for.</summary>
public enum SeoDnsType
{
    A,
    Aaaa,
    Txt,
    Caa,
    Ns,
    Ds,
    Mx,
}

/// <summary>
/// DNS behind one abstraction (<c>docs/seo_analyzer_PRD.md</c> F3 and the crawl's domain checks, research §3.5), so
/// tests answer from a table and nothing in them leaves the machine. What it returns is untrusted: callers compare
/// exactly and bound what they read.
/// </summary>
public interface ISeoDns
{
    /// <summary>The TXT records at <paramref name="name"/>, each its strings concatenated; empty when there are none or the lookup failed.</summary>
    Task<IReadOnlyList<string>> GetTxtRecordsAsync(string name, CancellationToken cancellationToken);

    /// <summary>
    /// The records of <paramref name="type"/> at <paramref name="name"/> as text (an address, a host name, a TXT
    /// string, a CAA <c>flags tag "value"</c>, a DS digest); empty when the name has none; <c>null</c> when the lookup failed.
    /// </summary>
    Task<IReadOnlyList<string>?> QueryAsync(string name, SeoDnsType type, CancellationToken cancellationToken);

    /// <summary>The zone <paramref name="host"/> belongs to (the owner of the SOA that answers for it), or null.</summary>
    Task<string?> ZoneApexAsync(string host, CancellationToken cancellationToken);
}

/// <summary>The system resolvers through DnsClient: short timeouts, one retry, no cache (TTLs matter for re-checks).</summary>
internal sealed class DnsClientSeoDns(ILogger<DnsClientSeoDns> logger) : ISeoDns
{
    /// <summary>Most records read per answer.</summary>
    private const int MaxRecords = 100;

    private readonly LookupClient client = new(new LookupClientOptions
    {
        Timeout = TimeSpan.FromSeconds(5),
        Retries = 1,
        UseCache = false,
        ThrowDnsErrors = false,
        ContinueOnDnsError = true,
    });

    public async Task<IReadOnlyList<string>> GetTxtRecordsAsync(string name, CancellationToken cancellationToken)
        => await QueryAsync(name, SeoDnsType.Txt, cancellationToken) ?? [];

    public async Task<IReadOnlyList<string>?> QueryAsync(string name, SeoDnsType type, CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.QueryAsync(name, QueryTypeOf(type), QueryClass.IN, cancellationToken);
            if (response.HasError)
                return response.Header.ResponseCode == DnsHeaderResponseCode.NotExistentDomain ? [] : null;
            IEnumerable<string> values = type switch
            {
                SeoDnsType.A => response.Answers.ARecords().Select(r => r.Address.ToString()),
                SeoDnsType.Aaaa => response.Answers.AaaaRecords().Select(r => r.Address.ToString()),
                SeoDnsType.Txt => response.Answers.TxtRecords().Select(r => string.Concat(r.Text)),
                SeoDnsType.Caa => response.Answers.CaaRecords().Select(r => $"{r.Flags} {r.Tag} \"{r.Value}\""),
                SeoDnsType.Ns => response.Answers.NsRecords().Select(r => r.NSDName.Value.TrimEnd('.')),
                SeoDnsType.Ds => response.Answers.OfType<DsRecord>().Select(r => $"{r.KeyTag} {r.Algorithm} {r.DigestType}"),
                SeoDnsType.Mx => response.Answers.MxRecords().Select(r => r.Exchange.Value.TrimEnd('.')),
                _ => [],
            };
            return values.Take(MaxRecords).ToList();
        }
        catch (Exception ex) when (ex is DnsResponseException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("{Type} lookup of {Name} failed: {Message}", type, name, ex.Message);
            return null;
        }
    }

    public async Task<string?> ZoneApexAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.QueryAsync(host, QueryType.SOA, QueryClass.IN, cancellationToken);
            var soa = response.Answers.SoaRecords().FirstOrDefault() ?? response.Authorities.SoaRecords().FirstOrDefault();
            return soa?.DomainName.Value.TrimEnd('.').ToLowerInvariant();
        }
        catch (Exception ex) when (ex is DnsResponseException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("SOA lookup of {Name} failed: {Message}", host, ex.Message);
            return null;
        }
    }

    private static QueryType QueryTypeOf(SeoDnsType type) => type switch
    {
        SeoDnsType.A => QueryType.A,
        SeoDnsType.Aaaa => QueryType.AAAA,
        SeoDnsType.Txt => QueryType.TXT,
        SeoDnsType.Caa => QueryType.CAA,
        SeoDnsType.Ns => QueryType.NS,
        SeoDnsType.Ds => QueryType.DS,
        _ => QueryType.MX,
    };
}
