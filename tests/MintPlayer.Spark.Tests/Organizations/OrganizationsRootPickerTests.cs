using System.Net;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Organizations;
using MintPlayer.Spark.Organizations.Models;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Organizations;

/// <summary>
/// The SeoTester trigger's shape (<c>docs/default_index_PRD.md</c> §1, SP3): a site's Organization picker was empty because
/// the query-less <c>[Reference(typeof(Organization))]</c> had no query. The fix is an <c>Organizations</c> root on the
/// application's context, whose minted <c>GetOrganizations</c> runs over the package's type. That must list exactly what
/// the package's own <c>Custom.Organizations</c> lists, under the package's row policy, for every kind of caller: otherwise
/// the picker would offer an organization the caller cannot see, or hide one the caller may pick.
/// </summary>
public class OrganizationsRootPickerTests : OrganizationsTestHost
{
    private const string RootQueryId = "114077de-5fb7-4c39-ad61-9bd2ea177d7d";

    /// <summary>
    /// The application layer synchronize writes over the package's <c>Organization.json</c>: SeoTester's
    /// <c>App_Data/Model/Organization.json</c> as committed in M8. The minted query steps aside from the package's alias
    /// (<c>organizations-2</c>, D11), and the audit-named <c>CreatedBy</c> is created hidden by the default hook (D9).
    /// </summary>
    private const string ApplicationLayer = """
        {
          "persistentObject": {
            "name": "Organization",
            "attributes": [
              { "id": "333a0610-12cc-4b97-9711-a1529680ee47", "name": "CreatedBy", "dataType": "string",
                "isRequired": false, "isReadOnly": true, "order": 4, "isArray": false, "showedOn": "None", "rules": [] }
            ]
          },
          "queries": [
            {
              "id": "114077de-5fb7-4c39-ad61-9bd2ea177d7d",
              "name": "GetOrganizations",
              "source": "Database.Organizations",
              "alias": "organizations-2",
              "sortColumns": [ { "property": "Name", "direction": "asc" } ],
              "renderMode": "Pagination",
              "entityType": "Organization"
            }
          ]
        }
        """;

    public OrganizationsRootPickerTests()
    {
        // Inside AddSpark, before the host loads the model and before the factory writes the hash file.
        ExtraSpark = spark =>
        {
            var environment = (IHostEnvironment)spark.Services.Last(d => d.ServiceType == typeof(IHostEnvironment)).ImplementationInstance!;
            File.WriteAllText(Path.Combine(SparkAppData.Path(environment.ContentRootPath, "Model"), "Organization.json"), ApplicationLayer);
        };
    }

    private async Task<(HttpStatusCode Status, string Ids)> RunAsync(string queryId, string? userId)
    {
        var (status, body) = await SendAsync("/spark/queries/execute", Wire.Query(queryId), userId);
        var ids = status == HttpStatusCode.OK
            ? string.Join(",", body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()).Order())
            : "-";
        return (status, ids);
    }

    [Fact]
    public async Task The_root_query_lists_exactly_what_the_package_query_lists_for_every_caller()
    {
        var ann = await SeedUserAsync("ann");
        var vic = await SeedUserAsync("vic");
        var own = await SeedUserAsync("own");
        var nob = await SeedUserAsync("nob");
        var acme = await SeedOrganizationAsync("Acme", ann, (vic, OrganizationRole.Viewer));
        var other = await SeedOrganizationAsync("Other", own);
        using (var session = Store.OpenAsyncSession())
            await session.Query<Organization>().Customize(c => c.WaitForNonStaleResults()).ToListAsync();

        var callers = new (string Label, string? UserId, HttpStatusCode Status, string Ids)[]
        {
            ("member", vic.Id, HttpStatusCode.OK, acme.Id!),
            ("owner", own.Id, HttpStatusCode.OK, other.Id!),
            ("no organization", nob.Id, HttpStatusCode.OK, ""),
            ("anonymous", null, HttpStatusCode.Unauthorized, "-"),
        };
        foreach (var (label, userId, status, ids) in callers)
        {
            var package = await RunAsync(OrganizationsQueryId, userId);
            var root = await RunAsync(RootQueryId, userId);

            root.Should().Be(package, $"the {label} sees the same organizations in the picker as on the package's own list");
            // Pinned too, so parity cannot pass by both being wrong the same way.
            root.Should().Be((status, ids), label);
        }
    }
}
