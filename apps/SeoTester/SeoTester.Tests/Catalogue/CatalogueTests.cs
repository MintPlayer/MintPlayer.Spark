using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Testing;
using SeoTester.Catalogue;
using SeoTester.Entities;
using SeoTester.Tests._Infrastructure;
using static SeoTester.Tests._Infrastructure.SeoApi;

namespace SeoTester.Tests.Catalogue;

/// <summary>
/// The runtime catalogue (<c>docs/seo_analyzer_PRD.md</c> §3.1, D32): seeded from the shipped JSON without ever
/// overwriting an administrator's edit, readable by every signed-in user, edited and reset by administrators only.
/// </summary>
[Collection(SeoTesterHostCollection.Name)]
public class CatalogueTests(SeoTesterHostFixture host)
{
    private const string Administrators = "Administrators";

    [Fact]
    public async Task The_host_seeds_every_shipped_check_at_startup()
    {
        var shipped = ShippedCatalogue.All;
        using var session = host.Database.OpenAsyncSession();
        var stored = await session.LoadAsync<CheckDefinition>(shipped.Select(c => CheckDefinition.IdFor(c.Id)));

        foreach (var (id, definition) in stored)
        {
            definition.Should().NotBeNull(id);
            CheckDefinitionMapping.IsUnedited(definition).Should().BeTrue(id);
        }
    }

    [Fact]
    public async Task The_seeder_adds_new_checks_follows_the_shipped_text_of_unedited_ones_and_never_overwrites_an_edit()
    {
        var seeder = new CatalogueSeeder(host.Database, NullLogger<CatalogueSeeder>.Instance);
        var unedited = Shipped("seo.zz-unedited-" + Suffix(), "First");
        var edited = Shipped("seo.zz-edited-" + Suffix(), "First");

        (await seeder.SeedAsync([unedited, edited], CancellationToken.None)).Should().Be((2, 0));
        await EditAsync(edited.Id, d => d.TitleEn = "An administrator's wording");

        unedited.Title.En = edited.Title.En = "Second";
        var fresh = Shipped("seo.zz-new-" + Suffix(), "New");
        (await seeder.SeedAsync([unedited, edited, fresh], CancellationToken.None)).Should().Be((1, 1));
        (await seeder.SeedAsync([unedited, edited, fresh], CancellationToken.None)).Should().Be((0, 0), "a second pass changes nothing");

        (await LoadDocumentAsync<CheckDefinition>(host, CheckDefinition.IdFor(unedited.Id)))!.TitleEn.Should().Be("Second");
        (await LoadDocumentAsync<CheckDefinition>(host, CheckDefinition.IdFor(edited.Id)))!.TitleEn.Should().Be("An administrator's wording");
        (await LoadDocumentAsync<CheckDefinition>(host, CheckDefinition.IdFor(fresh.Id)))!.TitleEn.Should().Be("New");
    }

    /// <summary>
    /// Anyone reads the catalogue, anonymous visitors included: the same public text <c>GET /seotester/catalogue</c> serves,
    /// granted to the anonymous group so an AI agent's <c>seotester_explain_check</c> works before signing in
    /// (<c>docs/spark_mcp_PRD.md</c> §4.7, D17). Only an administrator edits it (D32).
    /// </summary>
    [Fact]
    public async Task Everyone_reads_the_catalogue_and_only_an_administrator_edits_it()
    {
        var id = CheckDefinition.IdFor(ShippedCatalogue.All[0].Id);

        var list = await PostAsync(host, "/spark/queries/execute", Wire.Query(CheckDefinitionsQueryId), Vic);
        list.Status.Should().Be(HttpStatusCode.OK, list.Raw);
        list.Body.GetProperty("totalItems").GetInt32().Should().BeGreaterThan(80);
        var anonymous = await PostAsync(host, "/spark/queries/execute", Wire.Query(CheckDefinitionsQueryId), null);
        anonymous.Status.Should().Be(HttpStatusCode.OK, anonymous.Raw);
        anonymous.Body.GetProperty("totalItems").GetInt32().Should().Be(list.Body.GetProperty("totalItems").GetInt32());

        (await UpdateAsync(host, CheckDefinitionTypeId, "CheckDefinition", id, Ann, ("Weight", 1))).Status.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound);
        (await PostAsync(host, "/spark/po/update", Wire.Typed(CheckDefinitionTypeId, new
        {
            persistentObject = new { id, name = "CheckDefinition", objectTypeId = CheckDefinitionTypeId, attributes = new[] { new { name = "Weight", value = (object)1, isValueChanged = true } } },
        }, id), null)).Status.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_administrator_edits_a_check_keeps_its_id_and_resets_it_to_the_shipped_version()
    {
        var shipped = ShippedCatalogue.All[1];
        var id = CheckDefinition.IdFor(shipped.Id);

        // As the edit form does: the loaded object, two values changed, posted back whole.
        var loaded = await LoadAsync(host, CheckDefinitionTypeId, id, "SparkUsers/admin", Administrators);
        loaded.Status.Should().Be(HttpStatusCode.OK, loaded.Raw);
        var po = System.Text.Json.Nodes.JsonNode.Parse(loaded.Raw)!.AsObject();
        if (po["persistentObject"] is System.Text.Json.Nodes.JsonObject inner)
            po = inner;
        foreach (var attribute in po["attributes"]!.AsArray())
        {
            var name = attribute!["name"]!.GetValue<string>();
            if (name is "Weight" or "CheckId")
            {
                attribute["value"] = name == "Weight" ? 1 : "seo.renamed";
                attribute["isValueChanged"] = true;
            }
        }
        var edit = await PostAsync(host, "/spark/po/update", Wire.Typed(CheckDefinitionTypeId, new { persistentObject = po }, id), "SparkUsers/admin", Administrators);
        edit.Status.Should().Be(HttpStatusCode.OK, edit.Raw);
        var edited = (await LoadDocumentAsync<CheckDefinition>(host, id))!;
        edited.Weight.Should().Be(1);
        edited.CheckId.Should().Be(shipped.Id, "implementations are keyed by it");
        CheckDefinitionMapping.IsUnedited(edited).Should().BeFalse();

        var reset = await PostAsync(host, "/spark/actions/execute",
            Wire.Action(CheckDefinitionTypeId, "ResetCheckDefinition", new { parent = new { id, objectTypeId = CheckDefinitionTypeId, name = "CheckDefinition" } }),
            "SparkUsers/admin", Administrators);
        reset.Status.Should().Be(HttpStatusCode.OK, reset.Raw);
        var restored = (await LoadDocumentAsync<CheckDefinition>(host, id))!;
        restored.Weight.Should().Be(shipped.Weight);
        CheckDefinitionMapping.IsUnedited(restored).Should().BeTrue();

        (await PostAsync(host, "/spark/actions/execute",
            Wire.Action(CheckDefinitionTypeId, "ResetCheckDefinition", new { parent = new { id, objectTypeId = CheckDefinitionTypeId, name = "CheckDefinition" } }), Ann))
            .Status.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_administrator_creates_a_check_whose_id_is_its_check_id()
    {
        var checkId = "seo.zz-custom-" + Suffix();

        var (status, body, raw) = await PostAsync(host, "/spark/po/create", Wire.Typed(CheckDefinitionTypeId, new
        {
            persistentObject = new
            {
                name = "CheckDefinition",
                objectTypeId = CheckDefinitionTypeId,
                attributes = new object[]
                {
                    new { name = "CheckId", value = checkId, isValueChanged = true },
                    new { name = "Category", value = "Seo", isValueChanged = true },
                    new { name = "Severity", value = "Low", isValueChanged = true },
                    new { name = "Weight", value = 1, isValueChanged = true },
                    new { name = "TitleEn", value = "Custom", isValueChanged = true },
                    new { name = "TitleNl", value = "Eigen", isValueChanged = true },
                },
            },
        }), "SparkUsers/admin", Administrators);

        status.Should().Be(HttpStatusCode.Created, raw);
        body.GetProperty("result").GetProperty("id").GetString().Should().Be(CheckDefinition.IdFor(checkId));
        (await LoadDocumentAsync<CheckDefinition>(host, CheckDefinition.IdFor(checkId)))!.SeededHash.Should().BeNull("an administrator's entry is never the seeder's");
    }

    private static ShippedCheck Shipped(string id, string title) => new()
    {
        Id = id,
        Category = CheckCategory.Seo,
        Severity = CheckSeverity.Low,
        Weight = 1,
        Layer = "H",
        Title = new LocalizedText { En = title, Nl = title },
        Summary = new LocalizedText { En = "s", Nl = "s" },
        HowToFix = new LocalizedText { En = "h", Nl = "h" },
    };

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private async Task EditAsync(string checkId, Action<CheckDefinition> edit)
    {
        using var session = host.Database.OpenAsyncSession();
        edit(await session.LoadAsync<CheckDefinition>(CheckDefinition.IdFor(checkId)));
        await session.SaveChangesAsync();
    }
}
