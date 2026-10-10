using System.Diagnostics;
using System.Net;
using System.Text;
using MintPlayer.Spark.Organizations;
using MintPlayer.Spark.Organizations.Models;
using MintPlayer.Spark.Organizations.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;
using Xunit.Abstractions;

namespace MintPlayer.Spark.Tests.Organizations;

/// <summary>
/// Spike S13 (<c>docs/seo_analyzer_PRD.md</c> §8): what the membership filter costs and how it composes.
/// 10 000 organization-owned documents (<see cref="OgNote"/>, which also expires) spread over 200
/// organizations, users in 0, 1, 10 and 100 of them. Measured, with medians written to the test output
/// and recorded in the PRD (§12 "Spike S13"):
/// <list type="bullet">
/// <item>(a) the per-request membership lookup the package uses, by id prefix, against the alternative of a
/// query on <c>UserId</c> (an auto-index);</item>
/// <item>the RQL the composed filter produces with the expiration policy, and its query time through the
/// real route table (one grid page) and as a raw query;</item>
/// <item>(b) a claim carrying the organization ids: its size per user;</item>
/// <item>(c) a <c>load</c> in the query: what RavenDB answers.</item>
/// </list>
/// </summary>
public class OrganizationFilterSpikeTests(ITestOutputHelper output) : OrganizationsTestHost
{
    private const int Organizations = 200;
    private const int DocumentsPerOrganization = 50;
    private const int Runs = 15;

    private static string OrgId(int i) => $"Organizations/spike-{i:D3}";

    [Fact]
    public async Task The_membership_filter_costs_one_prefix_load_and_an_in_clause()
    {
        _ = Factory;
        var users = new Dictionary<int, string>();
        foreach (var n in new[] { 0, 1, 10, 100 })
            users[n] = (await SeedUserAsync("u" + n)).Id!;

        using (var bulk = Store.BulkInsert())
        {
            for (var o = 0; o < Organizations; o++)
            {
                for (var d = 0; d < DocumentsPerOrganization; d++)
                {
                    // A fifth of them carry a future expiry, so the expiration predicate has data to read.
                    await bulk.StoreAsync(new OgNote
                    {
                        Title = $"note {o}/{d}",
                        OrganizationId = OrgId(o),
                        ExpiresAtUtc = d % 5 == 0 ? DateTime.UtcNow.AddDays(7) : null,
                    }, $"OgNotes/{o:D3}-{d:D3}");
                }
            }
            foreach (var (n, userId) in users)
            {
                for (var o = 0; o < n; o++)
                {
                    await bulk.StoreAsync(new OrganizationMembership
                    {
                        Id = OrganizationMembership.IdFor(userId, OrgId(o)),
                        OrganizationId = OrgId(o),
                        UserId = userId,
                        UserName = "u" + n,
                        Role = OrganizationRole.Viewer,
                        JoinedAtUtc = DateTime.UtcNow,
                    }, OrganizationMembership.IdFor(userId, OrgId(o)));
                }
            }
        }

        // Warm the auto-indexes both shapes ride, so the timings measure queries, not index builds.
        var now = DateTime.UtcNow;
        using (var warm = Store.OpenAsyncSession())
        {
            var some = new[] { OrgId(0) };
            _ = await warm.Query<OgNote>().Customize(c => c.WaitForNonStaleResults(TimeSpan.FromMinutes(2)))
                .Where(x => x.OrganizationId.In(some) && !(x.ExpiresAtUtc <= now)).Take(1).ToListAsync();
            _ = await warm.Query<OrganizationMembership>().Customize(c => c.WaitForNonStaleResults(TimeSpan.FromMinutes(2)))
                .Where(m => m.UserId == users[1]).ToListAsync();
        }

        var report = new StringBuilder();
        report.AppendLine($"{Organizations * DocumentsPerOrganization} OgNotes over {Organizations} organizations; medians of {Runs} runs after 3 warm-ups");
        // Two passes: the first also pays every first use (index selection, the host's caches); the second is reported.
        foreach (var (pass, (n, userId)) in new[] { 1, 2 }.SelectMany(p => users.Select(u => (p, (u.Key, u.Value)))))
        {
            var prefix = await MedianAsync(async () =>
            {
                using var session = Store.OpenAsyncSession();
                return (await OrganizationMembershipReader.LoadAsync(session, userId, CancellationToken.None)).Count;
            }, n);
            var byIndex = await MedianAsync(async () =>
            {
                using var session = Store.OpenAsyncSession();
                return (await session.Query<OrganizationMembership>().Where(m => m.UserId == userId).Take(1024).ToListAsync()).Count;
            }, n);

            var ids = Enumerable.Range(0, n).Select(OrgId).ToArray();
            var raw = n == 0 ? 0 : await MedianAsync(async () =>
            {
                using var session = Store.OpenAsyncSession();
                var page = await session.Query<OgNote>()
                    .Statistics(out var stats)
                    .Where(x => x.OrganizationId.In(ids) && !(x.ExpiresAtUtc <= now))
                    .Take(25).ToListAsync();
                return (int)stats.TotalResults;
            }, n * DocumentsPerOrganization);

            var http = await MedianAsync(async () =>
            {
                var (status, body) = await SendAsync("/spark/queries/execute", Wire.Query(NotesQueryId, new { take = 25 }), userId);
                status.Should().Be(HttpStatusCode.OK);
                return body.GetProperty("totalItems").GetInt32();
            }, n * DocumentsPerOrganization);

            // (b): the organization ids as one claim value per membership, as a cookie would carry them.
            var claimBytes = Encoding.UTF8.GetByteCount(string.Join(",", ids));

            report.AppendLine($"pass {pass} N={n,3}: memberships by prefix {prefix,6:0.00} ms | by UserId query {byIndex,6:0.00} ms | "
                + $"raw page {raw,6:0.00} ms | HTTP grid page {http,6:0.00} ms | claim {claimBytes,5} B | visible {n * DocumentsPerOrganization}");
        }

        using (var session = Store.OpenAsyncSession())
        {
            var ids = new[] { OrgId(0), OrgId(1) };
            var rql = session.Query<OgNote>().Where(x => x.OrganizationId.In(ids) && !(x.ExpiresAtUtc <= now)).ToString();
            report.AppendLine("RQL (raw equivalent of the composed policies): " + rql);
        }

        // (c): a load in the filter. RQL loads documents for the projection only.
        using (var session = Store.OpenAsyncSession())
        {
            try
            {
                await session.Advanced.AsyncRawQuery<OgNote>("from OgNotes as n load n.OrganizationId as o where o.Name = $name")
                    .AddParameter("name", "x").ToListAsync();
                report.AppendLine("(c) load in where: accepted");
            }
            catch (Exception ex)
            {
                report.AppendLine("(c) load in where: " + ex.GetType().Name + ": " + ex.Message.Split('\n')[0]);
            }
        }

        output.WriteLine(report.ToString());
    }

    /// <summary>Median milliseconds of <paramref name="work"/>, which must return <paramref name="expected"/> every time.</summary>
    private static async Task<double> MedianAsync(Func<Task<int>> work, int expected)
    {
        for (var i = 0; i < 3; i++)
            (await work()).Should().Be(expected);
        var times = new List<double>();
        for (var i = 0; i < Runs; i++)
        {
            var watch = Stopwatch.StartNew();
            var count = await work();
            watch.Stop();
            count.Should().Be(expected);
            times.Add(watch.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        return times[times.Count / 2];
    }
}
