using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Commerce.Invoicing.EInvoicing;

namespace MintPlayer.Spark.Commerce.Invoicing.Peppol.Test;

/// <summary>What the test access point does.</summary>
public sealed class TestPeppolOptions
{
    /// <summary>Receivers that can receive; empty: everyone can.</summary>
    public HashSet<string> Directory { get; } = new(StringComparer.Ordinal);

    /// <summary>Receivers whose documents are refused (a validation failure at the access point; not retryable).</summary>
    public HashSet<string> Refuse { get; } = new(StringComparer.Ordinal);

    /// <summary>The next N sends fail with a retryable outage (HTTP 503 at a real access point).</summary>
    public int OutagesRemaining { get; set; }
}

/// <summary>A document the test access point was handed.</summary>
public sealed record TestPeppolSubmission(string Reference, PeppolSendRequest Request, DateTimeOffset At);

/// <summary>
/// The in-memory access point (<c>test</c>): sends nothing, records every submission (<see cref="Submissions"/>), answers
/// lookups from <see cref="TestPeppolOptions.Directory"/>, refuses or fails as configured. Singleton, so a test or a demo
/// reads what was sent.
/// </summary>
public sealed class TestPeppolAccessPoint(TestPeppolOptions options, TimeProvider time) : IPeppolAccessPoint
{
    public const string ProviderName = "test";

    private readonly ConcurrentDictionary<string, TestPeppolSubmission> submissions = new(StringComparer.Ordinal);

    public string Name => ProviderName;

    public IReadOnlyCollection<TestPeppolSubmission> Submissions => submissions.Values.OrderBy(s => s.At).ToList();

    public Task<PeppolLookupResult> LookupAsync(PeppolParticipant receiver, string documentTypeId, CancellationToken cancellationToken)
        => Task.FromResult(options.Directory.Count == 0 || options.Directory.Contains(receiver.ToString())
            ? new PeppolLookupResult(true)
            : new PeppolLookupResult(false, "test: not in the directory."));

    public Task<PeppolSendResult> SendAsync(PeppolSendRequest request, CancellationToken cancellationToken)
    {
        lock (options)
        {
            if (options.OutagesRemaining > 0)
            {
                options.OutagesRemaining--;
                return Task.FromResult(new PeppolSendResult(false, null, "test: simulated outage (503).", Retryable: true));
            }
        }
        if (options.Refuse.Contains(request.Receiver.ToString()))
            return Task.FromResult(new PeppolSendResult(false, null, "test: refused by the access point."));
        // Idempotent by our document id, like a real access point's client reference.
        var submission = submissions.GetOrAdd(request.InvoiceId, _ => new TestPeppolSubmission("test-" + submissions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), request, time.GetUtcNow()));
        return Task.FromResult(new PeppolSendResult(true, submission.Reference));
    }
}

/// <summary>Registration of the test access point.</summary>
public static class TestPeppolExtensions
{
    /// <summary>Adds the in-memory access point (<c>test</c>). Never use it in production: it sends nothing.</summary>
    public static SparkInvoicingBuilder UseTestPeppol(this SparkInvoicingBuilder invoicing, Action<TestPeppolOptions>? configure = null)
    {
        var options = new TestPeppolOptions();
        configure?.Invoke(options);
        var services = invoicing.Spark.Services;
        services.AddSingleton(options);
        services.AddSingleton<TestPeppolAccessPoint>();
        services.AddScoped<IPeppolAccessPoint>(sp => sp.GetRequiredService<TestPeppolAccessPoint>());
        return invoicing.Spark.AddCommerceInvoicing();
    }
}
