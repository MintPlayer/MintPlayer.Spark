using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Testing;
using Raven.Client.Http;
using Sparrow.Json;
using Raven.Client.Documents.Operations;
using Xunit.Abstractions;

namespace MintPlayer.Spark.Tests.Expiration;

/// <summary>
/// Spike S6 (<c>docs/seo_analyzer_PRD.md</c> §8): what a run's per-page results cost in RavenDB while they
/// wait for their 7-day <c>@expires</c>. A <see cref="SpPageResult"/> has the shape §4.2 gives the
/// SeoTester <c>PageResult</c>: run, URL, status, depth, timings, the title/description/h1 snapshot and
/// ~20 findings. Sizes are measured, not estimated; the numbers are written to the test output and
/// recorded in the PRD (§12 "Spike S6").
/// </summary>
public class ExpirationStorageSpikeTests(ITestOutputHelper output) : SparkTestDriver
{
    [Fact]
    public async Task A_page_result_with_twenty_findings_and_a_2000_page_run_stay_small()
    {
        using (var bulk = Store.BulkInsert())
        {
            // A bulk insert raises no OnBeforeStore, so @expires is written here as the stamper would.
            for (var i = 0; i < 2000; i++)
            {
                var page = Representative(i);
                await bulk.StoreAsync(page, $"SpPageResults/{i:D5}", new Raven.Client.Json.MetadataAsDictionary
                {
                    [Raven.Client.Constants.Documents.Metadata.Expires] = page.ExpiresAtUtc!.Value.ToString("O"),
                });
            }
        }

        var context = Store.GetRequestExecutor().ContextPool.AllocateOperationContext(out var json);
        using (context)
        {
            var command = new DocumentSizeCommand("SpPageResults/00042");
            await Store.GetRequestExecutor().ExecuteAsync(command, json);
            var size = command.Result!;

            var stats = await Store.Maintenance.SendAsync(new GetDetailedCollectionStatisticsOperation());
            var collection = stats.Collections[Store.Conventions.FindCollectionName(typeof(SpPageResult))];
            var perDocument = (double)collection.DocumentsSize.SizeInBytes / collection.CountOfDocuments;

            output.WriteLine($"one PageResult (20 findings): actual {size.ActualSize} B, allocated {size.AllocatedSize} B");
            output.WriteLine($"2000 PageResults: documents {collection.DocumentsSize.SizeInBytes} B ({perDocument:0} B/doc), " +
                             $"total incl. tombstones/revisions {collection.Size.SizeInBytes} B");
            output.WriteLine($"100-page run ~ {perDocument * 100 / 1024:0} KiB, 2000-page run ~ {perDocument * 2000 / 1024 / 1024:0.0} MiB");

            size.ActualSize.Should().BeLessThan(4096, "§4.2 budgets 1-3 KB per page");
            perDocument.Should().BeLessThan(4096);
        }
    }

    private static SpPageResult Representative(int i) => new()
    {
        RunId = "AuditRuns/0f9c4b1e-7d2a-4f4e-9a51-3c2b8e6d1a77",
        Url = $"https://www.example-shop.be/nl/categorie/tuinmeubelen/loungesets/product-{i:D5}?kleur=antraciet",
        StatusCode = 200,
        Depth = 3,
        FetchMs = 412,
        TtfbMs = 188,
        Bytes = 84_213,
        Title = "Loungeset Antraciet 5-delig | Weerbestendig aluminium | Example Shop",
        Description = "Ontdek onze 5-delige loungeset in antraciet: weerbestendig aluminium, comfortabele kussens en gratis levering in België. Bestel vandaag, morgen in huis.",
        H1 = "Loungeset Antraciet 5-delig",
        Canonical = $"https://www.example-shop.be/nl/categorie/tuinmeubelen/loungesets/product-{i:D5}",
        ExpiresAtUtc = DateTime.UtcNow.AddDays(7),
        Findings = Enumerable.Range(0, 20).Select(f => new SpFinding
        {
            CheckId = CheckIds[f],
            Status = f % 4 == 0 ? "fail" : f % 3 == 0 ? "warn" : "pass",
            Params = f % 2 == 0 ? new Dictionary<string, string> { ["length"] = "172", ["max"] = "160" } : null,
        }).ToList(),
    };

    /// <summary><c>GET /databases/{db}/docs/size?id=</c> (the client's own command is internal).</summary>
    private sealed class DocumentSizeCommand(string id) : RavenCommand<DocumentSize>
    {
        public override bool IsReadRequest => true;

        public override HttpRequestMessage CreateRequest(JsonOperationContext ctx, ServerNode node, out string url)
        {
            url = $"{node.Url}/databases/{node.Database}/docs/size?id={Uri.EscapeDataString(id)}";
            return new HttpRequestMessage { Method = HttpMethod.Get };
        }

        public override void SetResponse(JsonOperationContext context, BlittableJsonReaderObject? response, bool fromCache)
        {
            if (response is null) return;
            using var parsed = System.Text.Json.JsonDocument.Parse(response.ToString());
            Result = new DocumentSize(parsed.RootElement.GetProperty("ActualSize").GetInt64(), parsed.RootElement.GetProperty("AllocatedSize").GetInt64());
        }
    }

    private sealed record DocumentSize(long ActualSize, long AllocatedSize);

    private static readonly string[] CheckIds =
    [
        "seo.title-length", "seo.meta-description-length", "seo.h1-single", "seo.canonical-self", "seo.robots-indexable",
        "seo.hreflang-valid", "seo.img-alt", "seo.internal-links", "seo.structured-data-valid", "perf.response-time",
        "perf.page-weight", "perf.compression", "perf.cache-headers", "sec.https", "sec.hsts", "sec.mixed-content",
        "a11y.lang-attribute", "a11y.viewport", "social.og-complete", "social.twitter-card",
    ];
}

public class SpPageResult : IExpiring
{
    public string? Id { get; set; }
    public string RunId { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public int Depth { get; set; }
    public int FetchMs { get; set; }
    public int TtfbMs { get; set; }
    public long Bytes { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? H1 { get; set; }
    public string? Canonical { get; set; }
    public List<SpFinding> Findings { get; set; } = [];
    public DateTime? ExpiresAtUtc { get; set; }
}

public class SpFinding
{
    public string CheckId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public Dictionary<string, string>? Params { get; set; }
}
