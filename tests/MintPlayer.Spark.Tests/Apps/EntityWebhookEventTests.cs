using System.Net;
using MintPlayer.Spark.Apps;
using MintPlayer.Spark.Apps.Catalog;
using MintPlayer.Spark.Organizations;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using MintPlayer.Spark.Tests.Organizations;

namespace MintPlayer.Spark.Tests.Apps;

/// <summary>
/// #691 M6 (<c>docs/apps_webhooks_PRD.md</c> D31): <c>AddEntityEvents</c> raises one thin event per committed
/// create, edit and delete made through Spark, to the row's owner.
/// </summary>
public class EntityWebhookEventTests : AppsTestHost
{
    protected override void ConfigureAppsBuilder(AppsBuilder apps)
        => apps.AddEntityEvents<OgNote>(EntityEvents.All, "notes:read", note => note.OrganizationId is { } org ? WebhookOwner.Organization(org) : null, name: "note");

    [Fact]
    public async Task Create_edit_and_delete_each_raise_exactly_one_event_with_the_id_only()
    {
        var ann = await SeedUserAsync("ann");
        var acme = await SeedOrganizationAsync("Acme", ann);
        var (endpointId, _) = await CreateEndpointAsync(ann.Id!, WebhookOwner.Organization(acme.Id.Value), ["note.created", "note.updated", "note.deleted"]);

        var (created, body) = await SendAsync("/spark/po/create", Wire.Typed(NoteTypeId, new
        {
            persistentObject = new
            {
                name = "OgNote",
                objectTypeId = NoteTypeId.ToString(),
                attributes = new object[]
                {
                    new { name = "Title", value = "secret plans", isValueChanged = true },
                    new { name = "OrganizationId", value = acme.Id, isValueChanged = true },
                },
            },
        }), ann.Id);
        created.Should().Be(HttpStatusCode.Created, body.ToString());
        var id = body.GetProperty("result").GetProperty("id").GetString()!;
        await UntilSettledAsync(endpointId, 1);

        var (edited, _) = await SendAsync("/spark/po/update", Wire.Typed(NoteTypeId, new
        {
            persistentObject = new
            {
                id,
                etag = await StoredEtag.OfAsync(Store, id),
                name = "OgNote",
                objectTypeId = NoteTypeId.ToString(),
                attributes = new[] { new { name = "Title", value = "public plans", isValueChanged = true } },
            },
        }, id), ann.Id);
        edited.Should().Be(HttpStatusCode.OK);
        await UntilSettledAsync(endpointId, 2);

        var (deleted, _) = await SendAsync("/spark/po/delete", Wire.Typed(NoteTypeId, id: id, etag: await StoredEtag.OfAsync(Store, id)), ann.Id);
        deleted.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
        await UntilSettledAsync(endpointId, 3);
        await Task.Delay(500);

        var received = Receiver.Received.Select(r => r.Json).ToList();
        received.Select(j => j.GetProperty("type").GetString()).Should().Equal(["note.created", "note.updated", "note.deleted"], "one event per committed write, in order");
        received.Should().AllSatisfy(j => j.GetProperty("data").GetProperty("id").GetString().Should().Be(id));
        received.Select(j => j.GetRawText()).Should().NotContain(t => t.Contains("plans"), "the events are thin: the receiver reads the row with its own rights");
        received[2].GetProperty("data").GetProperty("soft").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void The_catalog_names_the_data_after_the_entity_and_a_rerun_keeps_its_webhook_id()
    {
        var catalog = Factory.GetService<WebhookEventCatalog>();
        catalog.Find("note.created")!.Permission.Should().Be("notes:read");
        catalog.Find("note.deleted")!.Schema["title"]!.GetValue<string>().Should().Be("OgNoteDeleted", "codegen names the class after the entity, not EntityDeleted`1");

        var change = new MintPlayer.Spark.Abstractions.Interceptors.SparkCommittedChange
        {
            EntityType = typeof(OgNote).FullName!,
            Id = "OgNotes/1",
            Operation = MintPlayer.Spark.Abstractions.Interceptors.PersistentObjectOperation.New,
            OccurredAt = DateTimeOffset.UtcNow,
        };
        var first = MintPlayer.Spark.Apps.Services.EntityWebhookInterceptor<OgNote>.MessageIdFor(change, new EntityCreated<OgNote>("OgNotes/1"));
        MintPlayer.Spark.Apps.Services.EntityWebhookInterceptor<OgNote>.MessageIdFor(change, new EntityCreated<OgNote>("OgNotes/1"))
            .Should().Be(first, "a re-run of the durable interceptor publishes under the same webhook-id");
        MintPlayer.Spark.Apps.Services.EntityWebhookInterceptor<OgNote>.MessageIdFor(change, new EntityDeleted<OgNote>("OgNotes/1", false))
            .Should().NotBe(first);
        first.Should().MatchRegex("^msg_[0-9a-f]{32}$");
    }
}
