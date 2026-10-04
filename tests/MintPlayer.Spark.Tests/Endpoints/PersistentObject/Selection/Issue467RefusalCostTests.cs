using System.Diagnostics;
using System.Net;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using Xunit.Abstractions;

namespace MintPlayer.Spark.Tests.Endpoints.PersistentObject.Selection;

/// <summary>
/// #467 spike S3: the cost of the D18 refusal message. A refused delete-many names every failed row by
/// its breadcrumb; those breadcrumbs must resolve in one batched pass, so a 200-row refusal costs the
/// same number of database requests as a 2-row one. Measured on the request executor the host shares
/// with this test, around the request alone.
/// </summary>
public class Issue467RefusalCostTests(ITestOutputHelper output) : SparkTestDriver
{
    private I467Host _host = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _host = await I467Host.StartAsync(Store);
    }

    public override async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        await base.DisposeAsync();
    }

    [Fact]
    public async Task S3_a_200_row_refusal_costs_no_more_requests_than_a_2_row_one()
    {
        var small = await MeasureAsync("I467Prompts/s", 2);
        var large = await MeasureAsync("I467Prompts/l", 200);

        output.WriteLine($"S3: 2 rows → {small.Requests} requests, {small.Elapsed.TotalMilliseconds:0} ms; " +
                         $"200 rows → {large.Requests} requests, {large.Elapsed.TotalMilliseconds:0} ms, {large.BodyLength} bytes");

        large.Requests.Should().BeLessThanOrEqualTo(small.Requests + 2,
            $"the refusal's breadcrumbs resolve in one batched pass, not one request per row (2 rows: {small.Requests}, 200 rows: {large.Requests})");
    }

    /// <summary>Seeds <paramref name="count"/> rows that each refuse inside a batch (a prompt, D33), and deletes them all.</summary>
    private async Task<(long Requests, TimeSpan Elapsed, int BodyLength)> MeasureAsync(string prefix, int count)
    {
        var ids = Enumerable.Range(0, count).Select(i => $"{prefix}{i}").ToArray();
        await SeedAsync(async session =>
        {
            foreach (var id in ids)
                await session.StoreAsync(new I467Prompt { Id = id, Name = $"row {id}" });
        });
        var body = Wire.Typed(I467Models.PromptTypeId, new
        {
            items = await StoredEtag.ItemsAsync(Store, ids),
            queryId = I467Models.PromptsQueryId.ToString(),
        });

        var executor = Store.GetRequestExecutor();
        var before = executor.NumberOfServerRequests;
        var clock = Stopwatch.StartNew();
        var (status, text) = await _host.SendAsync("/spark/po/delete-many", body);
        clock.Stop();
        var requests = executor.NumberOfServerRequests - before;

        status.Should().Be(HttpStatusCode.BadRequest, text);
        text.Should().Contain($"row {ids[^1]}", "D18: the refusal names every failed row by its breadcrumb");
        return (requests, clock.Elapsed, text.Length);
    }
}
