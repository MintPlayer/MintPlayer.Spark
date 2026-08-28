using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Endpoints.PersistentObject;

/// <summary>
/// The JSON-only virtual type read path (#324): a type that exists purely as a model file (no
/// <c>clrType</c>, no CLR class, no documents) is served by its name-resolved
/// <c>{Name}Actions</c> class through the PO-shaped <c>OnLoadAsync(PersistentObject)</c> hook —
/// under the type-level "Read" right, read-only, with no collection guard or row security (there
/// is no document to police). Entity-backed types are untouched: their <c>OnLoadAsync(session,
/// id)</c> pipeline is pinned down by the ordinary Get tests.
/// </summary>
public class VirtualObjectEndpointTests : SparkTestDriver
{
    private static readonly Guid PageTypeId = Guid.Parse("44444444-dddd-dddd-dddd-444444444444");

    // The JSON-only shape: no clrType at all — the type exists purely in the model, and its
    // actions are resolved by NAME (VirtualStartPage + "Actions").
    private static EntityTypeFile PageModel(Guid? id = null, string name = "VirtualStartPage") => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = id ?? PageTypeId,
            Name = name,
            Breadcrumb = "{Title}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Title", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Counter", DataType = "number" },
            ],
        }
    };

    private static async Task<Abstractions.PersistentObject?> GetPoAsync(SparkEndpointFactory factory, Guid typeId, string id)
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync($"/spark/po/{typeId}/{id}");
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<Abstractions.PersistentObject>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    [Fact]
    public async Task Virtual_type_is_served_by_its_name_resolved_OnLoadAsync_with_the_requested_id()
    {
        await using var factory = new SparkEndpointFactory(Store, [PageModel()]);

        var po = await GetPoAsync(factory, PageTypeId, "the-page");

        po.Should().NotBeNull("no CLR class exists anywhere for this type — JSON + a named Actions class suffice");
        po!.Id.Should().Be("the-page", "the scaffold's id is pre-set to the requested id");
        po.Breadcrumb.Should().Be("Loaded for the-page");
        po.Attributes.Should().Contain(a => a.Name == "Title" && a.Value!.ToString() == "Composed without a class");
        po.Attributes.Should().Contain(a => a.Name == "Counter" && a.Value!.ToString() == "42");
        po.Etag.Should().BeNull("there is no document, so there is no change vector");
    }

    [Fact]
    public async Task Virtual_object_is_forced_read_only_unless_the_hook_says_otherwise()
    {
        await using var factory = new SparkEndpointFactory(Store, [PageModel()]);

        var po = await GetPoAsync(factory, PageTypeId, "whatever");

        po!.Can.Should().NotBeNull("the framework stamps the affordances so the generic UI hides Edit/Delete");
        po.Can!.Edit.Should().BeFalse();
        po.Can.Delete.Should().BeFalse();
    }

    [Fact]
    public async Task Virtual_load_runs_under_the_type_level_Read_right()
    {
        var perms = Substitute.For<IPermissionService>();
        perms.IsAllowedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));
        perms.EnsureAuthorizedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(x => Task.FromException(new SparkAccessDeniedException($"{x.ArgAt<string>(0)}/{x.ArgAt<string>(1)}")));
        await using var factory = new SparkEndpointFactory(Store, [PageModel()], services =>
        {
            services.RemoveAll<IPermissionService>();
            services.AddSingleton(perms);
        });

        var po = await GetPoAsync(factory, PageTypeId, "the-page");

        po.Should().BeNull("no Read grant → 404, indistinguishable from missing (M-3)");
    }

    [Fact]
    public async Task Virtual_type_without_an_actions_class_returns_404()
    {
        // Same model shape, different name: nothing named OrphanPageActions exists, so the type
        // has no behavior at all — 404, not a blank page.
        var orphanId = Guid.Parse("55555555-eeee-eeee-eeee-555555555555");
        await using var factory = new SparkEndpointFactory(Store, [PageModel(orphanId, "OrphanPage")]);

        var po = await GetPoAsync(factory, orphanId, "anything");

        po.Should().BeNull();
    }
}

/// <summary>Found by name; plain class — no base class, no CLR entity anywhere. The framework
/// routes the virtual type's page load to the PO-shaped OnLoadAsync.</summary>
public sealed class VirtualStartPageActions
{
    public Task OnLoadAsync(Abstractions.PersistentObject obj)
    {
        obj["Title"].Value = "Composed without a class";
        obj["Counter"].Value = 42;
        obj.Breadcrumb = $"Loaded for {obj.Id}";
        return Task.CompletedTask;
    }
}
