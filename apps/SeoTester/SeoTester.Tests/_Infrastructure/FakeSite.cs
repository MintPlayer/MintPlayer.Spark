using System.Diagnostics;
using System.Net;
using Raven.Client.Documents;
using SeoTester.Entities;
using SeoTester.LookupReferences;

namespace SeoTester.Tests._Infrastructure;

/// <summary>Builds a small website in <see cref="FakeWeb"/> and the registries the probe asks, and waits for runs.</summary>
internal static class FakeSite
{
    public static string NewHost() => $"t{Guid.NewGuid():N}"[..13] + ".example.com";

    /// <summary>A page with a unique title and description, enough words, and the given links.</summary>
    public static string Page(string title, params string[] links)
        => $$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{title}}</title><meta name="description" content="{{title}}: a description long enough for search results, between seventy and one hundred sixty characters.">
            <link rel="manifest" href="/manifest.webmanifest"></head>
            <body><h1>{{title}}</h1><p>{{string.Join(' ', Enumerable.Range(0, 250).Select(i => title.GetHashCode().ToString("x") + i))}}</p>
            {{string.Concat(links.Select(l => $"<a href=\"{l}\">Read about {l}</a> "))}}</body></html>
            """;

    /// <summary>The IANA bootstrap, .com RDAP and hstspreload answers, from the recorded fixtures (spikes S4, S10).</summary>
    public static void Registries(FakeWeb web)
    {
        string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "Fixtures", name));
        web.Pages["https://data.iana.org/rdap/dns.json"] = new FakeResponse(HttpStatusCode.OK, Fixture("iana_dns.json"), ContentType: "application/json");
        web.Pages["https://rdap.verisign.com/com/v1/domain/example.com"] = new FakeResponse(HttpStatusCode.OK, Fixture("rdap_com.json"), ContentType: "application/rdap+json");
        web.Pages["https://hstspreload.org/api/v2/status?domain=example.com"] = new FakeResponse(HttpStatusCode.OK, Fixture("hsts_status_example.com.json"), ContentType: "application/json");
    }

    public static async Task<AuditRun> WaitForAsync(IDocumentStore store, string runId, Func<AuditRun, bool> until, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        AuditRun? run = null;
        while (watch.Elapsed < timeout)
        {
            using (var session = store.OpenAsyncSession())
                run = await session.LoadAsync<AuditRun>(runId);
            if (run is not null && until(run))
                return run;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Run {runId} did not get there within {timeout}: status {run?.Status}, phase {run?.Phase}, pages {run?.PagesDone}, error {run?.Error}.");
    }

    public static Task<AuditRun> FinishedAsync(IDocumentStore store, string runId, TimeSpan? timeout = null)
        => WaitForAsync(store, runId, r => AuditRunStatus.Finished.Contains(r.Status), timeout ?? TimeSpan.FromSeconds(90));
}
