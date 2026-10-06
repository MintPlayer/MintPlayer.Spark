using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Endpoints.Actions;
using MintPlayer.Spark.Models;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Tests._Infrastructure;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Endpoints.Actions;

public class ListCustomActionsTests
{
    private readonly IModelLoader _modelLoader = Substitute.For<IModelLoader>();
    private readonly IActionsCatalogueLoader _catalogueLoader = Substitute.For<IActionsCatalogueLoader>();
    private readonly ICustomActionResolver _actionResolver = Substitute.For<ICustomActionResolver>();
    private readonly IPermissionService _permissions = Substitute.For<IPermissionService>();

    private static readonly EntityTypeDefinition CarType = new()
    {
        Id = Guid.NewGuid(),
        Name = "Car",
        ClrType = "Fleet.Entities.Car",
    };

    /// <summary>
    /// The empty list, not a refusal. This is a catalogue endpoint and a known-but-denied type
    /// gets 200 with an empty list from the per-action filter, so answering 404 here would tell a
    /// caller which entity types exist — the M-3 oracle in a listing rather than in a load.
    /// </summary>
    [Fact]
    public async Task Returns_an_empty_list_when_entity_type_cannot_be_resolved()
    {
        _modelLoader.ResolveEntityType("unknown").Returns((EntityTypeDefinition?)null);
        var endpoint = NewEndpoint();
        var context = HttpContextWithRouteValues(("objectTypeId", "unknown"));

        var result = await endpoint.HandleAsync(context);

        (await ExecuteStatusAsync(result, context)).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Filters_out_definitions_whose_class_is_not_registered()
    {
        _modelLoader.ResolveEntityType(Arg.Any<string>()).Returns(CarType);
        UseCatalogue("""{ "Archive": { "offset": 1 }, "Unimplemented": { "offset": 2 } }""");
        _actionResolver.GetRegisteredActionNames().Returns(["Archive"]);
        _permissions.IsAllowedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        using var doc = await ListAsync();

        CustomActionNames(doc).Should().BeEquivalentTo(["Archive"]);
    }

    [Fact]
    public async Task Filters_out_actions_the_caller_is_not_permitted_to_execute()
    {
        _modelLoader.ResolveEntityType(Arg.Any<string>()).Returns(CarType);
        UseCatalogue("""{ "Allowed": { "offset": 1 }, "Denied": { "offset": 2 } }""");
        _actionResolver.GetRegisteredActionNames().Returns(["Allowed", "Denied"]);
        _permissions.IsAllowedAsync("Allowed", "Car", Arg.Any<CancellationToken>()).Returns(true);
        _permissions.IsAllowedAsync("Denied", "Car", Arg.Any<CancellationToken>()).Returns(false);

        using var doc = await ListAsync();

        CustomActionNames(doc).Should().BeEquivalentTo(["Allowed"]);
    }

    [Fact]
    public async Task Sorts_returned_actions_by_offset_ascending()
    {
        _modelLoader.ResolveEntityType(Arg.Any<string>()).Returns(CarType);
        UseCatalogue("""{ "Third": { "offset": 30 }, "First": { "offset": 10 }, "Second": { "offset": 20 } }""");
        _actionResolver.GetRegisteredActionNames().Returns(["First", "Second", "Third"]);
        _permissions.IsAllowedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        using var doc = await ListAsync();

        CustomActionNames(doc).Should().Equal("First", "Second", "Third");
    }

    /// <summary>
    /// The wire carries resolved text (#467, D26). This project compiles no app translations, so the
    /// label falls back to the humanized name and an explicit, untranslated confirmation key to the
    /// generic prompt; the description has no fallback and is absent.
    /// </summary>
    [Fact]
    public async Task Returned_shape_exposes_label_icon_flags_and_resolved_confirmation()
    {
        _modelLoader.ResolveEntityType(Arg.Any<string>()).Returns(CarType);
        UseCatalogue("""
            { "ArchiveCar": { "icon": "archive", "showedOn": "detail", "selectionRule": "=1",
              "refreshOnCompleted": true, "confirmation": "confirmArchive", "variant": "warning", "offset": 42 } }
            """);
        _actionResolver.GetRegisteredActionNames().Returns(["ArchiveCar"]);
        _permissions.IsAllowedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        using var doc = await ListAsync();

        var first = doc.RootElement.EnumerateArray().Single(e => !IsDefault(e));
        first.GetProperty("name").GetString().Should().Be("ArchiveCar");
        first.TryGetProperty("isDefault", out _).Should().BeFalse();
        first.GetProperty("label").GetProperty("en").GetString().Should().Be("Archive Car");
        first.GetProperty("icon").GetString().Should().Be("archive");
        (first.TryGetProperty("description", out var description) ? description.ValueKind : JsonValueKind.Null)
            .Should().Be(JsonValueKind.Null);
        first.GetProperty("showedOn").GetString().Should().Be("detail");
        first.GetProperty("selectionRule").GetString().Should().Be("=1");
        first.GetProperty("refreshOnCompleted").GetBoolean().Should().BeTrue();
        first.GetProperty("confirmation").GetProperty("en").GetString().Should().NotBeNullOrEmpty();
        first.GetProperty("variant").GetString().Should().Be("warning");
        first.GetProperty("offset").GetInt32().Should().Be(42);
        first.TryGetProperty("requiresClient", out _).Should().BeFalse();
    }

    /// <summary>
    /// <c>requiresClient</c> (generic passkeys page, Q9) reaches the client verbatim, so it can show the
    /// action disabled when the browser lacks the method; it is omitted when the action needs none.
    /// </summary>
    [Fact]
    public async Task RequiresClient_is_carried_to_the_client()
    {
        _modelLoader.ResolveEntityType(Arg.Any<string>()).Returns(CarType);
        UseCatalogue("""{ "ArchiveCar": { "requiresClient": "webauthn.create" } }""");
        _actionResolver.GetRegisteredActionNames().Returns(["ArchiveCar"]);
        _permissions.IsAllowedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        using var doc = await ListAsync();

        var action = doc.RootElement.EnumerateArray().Single(e => !IsDefault(e));
        action.GetProperty("requiresClient").GetString().Should().Be("webauthn.create");
    }

    // ── #467 D7: New, Edit and Delete come from the core layer, each under its own right ─────────

    [Fact]
    public async Task Edit_is_listed_for_a_caller_holding_Edit_on_the_type_and_only_then()
    {
        _modelLoader.ResolveEntityType(Arg.Any<string>()).Returns(CarType);
        UseCatalogue(null);
        _actionResolver.GetRegisteredActionNames().Returns([]);
        _permissions.IsAllowedAsync("Edit", "Car", Arg.Any<CancellationToken>()).Returns(true);

        using var allowed = await ListAsync();
        DefaultActionNames(allowed).Should().Equal("Edit");
        var edit = allowed.RootElement.EnumerateArray().Single();
        edit.GetProperty("selectionRule").GetString().Should().Be("=1");
        edit.GetProperty("showedOn").GetString().Should().Be("both");
        edit.GetProperty("icon").GetString().Should().Be("pencil");

        _permissions.IsAllowedAsync("Edit", "Car", Arg.Any<CancellationToken>()).Returns(false);
        using var denied = await ListAsync();
        DefaultActionNames(denied).Should().BeEmpty();
    }

    [Fact]
    public async Task An_app_layer_removes_a_built_in_with_null_and_overrides_another_per_property()
    {
        _modelLoader.ResolveEntityType(Arg.Any<string>()).Returns(CarType);
        UseCatalogue("""{ "Edit": null, "Delete": { "selectionRule": "=1" } }""");
        _actionResolver.GetRegisteredActionNames().Returns([]);
        _permissions.IsAllowedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        using var doc = await ListAsync();

        DefaultActionNames(doc).Should().Equal("New", "Delete");
        var delete = doc.RootElement.EnumerateArray().Single(e => e.GetProperty("name").GetString() == "Delete");
        delete.GetProperty("selectionRule").GetString().Should().Be("=1");
        // Not stated by the app, so inherited from the core layer.
        delete.GetProperty("variant").GetString().Should().Be("danger");
        delete.GetProperty("icon").GetString().Should().Be("trash");
    }

    private void UseCatalogue(string? appJson) => _catalogueLoader.GetCatalogue().Returns(TestActions.Catalogue(appJson));

    private async Task<JsonDocument> ListAsync()
    {
        var endpoint = NewEndpoint();
        var context = HttpContextWithRouteValues(("objectTypeId", CarType.Id.ToString()));
        var result = await endpoint.HandleAsync(context);
        return JsonDocument.Parse(await ExecuteBodyAsync(result, context));
    }

    private static string?[] DefaultActionNames(JsonDocument doc) =>
        [.. doc.RootElement.EnumerateArray().Where(IsDefault).Select(e => e.GetProperty("name").GetString())];

    /// <summary>
    /// The listed custom actions, in wire order. Since #460 D18 / #467 D7 the list also carries New, Edit and Delete
    /// (<c>isDefault: true</c>) for a caller holding those rights, which the permissive substitute
    /// above grants; the defaults are covered by <c>SubQueryActionsTests</c>, these cases by the
    /// custom-action catalogue.
    /// </summary>
    private static string?[] CustomActionNames(JsonDocument doc) =>
        [.. doc.RootElement.EnumerateArray().Where(e => !IsDefault(e)).Select(e => e.GetProperty("name").GetString())];

    private static bool IsDefault(JsonElement action) =>
        action.TryGetProperty("isDefault", out var flag) && flag.ValueKind == JsonValueKind.True;

    private ListCustomActions NewEndpoint() =>
        new(_modelLoader, _catalogueLoader, _actionResolver, _permissions);

    /// <summary>
    /// A request naming <paramref name="objectTypeId"/> in its body, as the literal route table takes
    /// it. The name kept its shape from when these were route values, because what the cases below
    /// are about — which actions a caller is shown — did not change with the transport.
    /// </summary>
    private static DefaultHttpContext HttpContextWithRouteValues(params (string Key, string Value)[] values)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
            // Authenticated: M-3 makes an unknown entity type answer exactly as a denied one, and
            // for an anonymous caller that is 401. These tests are about the 404 an authorized
            // caller sees, which is what makes "no such type" indistinguishable from "not yours".
            User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(authenticationType: "Test")),
        };
        var json = System.Text.Json.JsonSerializer.Serialize(
            values.ToDictionary(v => v.Key, v => v.Value),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = bytes.Length;

        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<HttpStatusCode> ExecuteStatusAsync(IResult result, HttpContext context)
    {
        await result.ExecuteAsync(context);
        return (HttpStatusCode)context.Response.StatusCode;
    }

    private static async Task<string> ExecuteBodyAsync(IResult result, HttpContext context)
    {
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        return await new StreamReader(context.Response.Body).ReadToEndAsync();
    }
}
