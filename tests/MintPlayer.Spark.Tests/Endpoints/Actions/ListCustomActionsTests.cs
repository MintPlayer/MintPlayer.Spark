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
using NSubstitute;

namespace MintPlayer.Spark.Tests.Endpoints.Actions;

public class ListCustomActionsTests
{
    private readonly IModelLoader _modelLoader = Substitute.For<IModelLoader>();
    private readonly ICustomActionsConfigurationLoader _configLoader = Substitute.For<ICustomActionsConfigurationLoader>();
    private readonly ICustomActionResolver _actionResolver = Substitute.For<ICustomActionResolver>();
    private readonly IPermissionService _permissions = Substitute.For<IPermissionService>();

    private static readonly EntityTypeDefinition CarType = new()
    {
        Id = Guid.NewGuid(),
        Name = "Car",
        ClrType = "Fleet.Entities.Car",
    };

    [Fact]
    public async Task Returns_404_when_entity_type_cannot_be_resolved()
    {
        _modelLoader.ResolveEntityType("unknown").Returns((EntityTypeDefinition?)null);
        var endpoint = NewEndpoint();
        var context = HttpContextWithRouteValues(("objectTypeId", "unknown"));

        var result = await endpoint.HandleAsync(context);

        (await ExecuteStatusAsync(result, context)).Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Filters_out_definitions_whose_class_is_not_registered()
    {
        _modelLoader.ResolveEntityType(Arg.Any<string>()).Returns(CarType);
        _configLoader.GetConfiguration().Returns(new CustomActionsConfiguration
        {
            ["Archive"] = NewDefinition("Archive", offset: 1),
            ["Unimplemented"] = NewDefinition("Unimplemented", offset: 2),
        });
        _actionResolver.GetRegisteredActionNames().Returns(["Archive"]);
        _permissions.IsAllowedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        var endpoint = NewEndpoint();
        var context = HttpContextWithRouteValues(("objectTypeId", CarType.Id.ToString()));

        var result = await endpoint.HandleAsync(context);
        var body = await ExecuteBodyAsync(result, context);

        using var doc = JsonDocument.Parse(body);
        doc.RootElement.EnumerateArray().Select(e => e.GetProperty("name").GetString())
            .Should().BeEquivalentTo(["Archive"]);
    }

    [Fact]
    public async Task Filters_out_actions_the_caller_is_not_permitted_to_execute()
    {
        _modelLoader.ResolveEntityType(Arg.Any<string>()).Returns(CarType);
        _configLoader.GetConfiguration().Returns(new CustomActionsConfiguration
        {
            ["Allowed"] = NewDefinition("Allowed", offset: 1),
            ["Denied"] = NewDefinition("Denied", offset: 2),
        });
        _actionResolver.GetRegisteredActionNames().Returns(["Allowed", "Denied"]);
        _permissions.IsAllowedAsync("Allowed", "Car", Arg.Any<CancellationToken>()).Returns(true);
        _permissions.IsAllowedAsync("Denied", "Car", Arg.Any<CancellationToken>()).Returns(false);

        var endpoint = NewEndpoint();
        var context = HttpContextWithRouteValues(("objectTypeId", CarType.Id.ToString()));

        var result = await endpoint.HandleAsync(context);
        var body = await ExecuteBodyAsync(result, context);

        using var doc = JsonDocument.Parse(body);
        doc.RootElement.EnumerateArray().Select(e => e.GetProperty("name").GetString())
            .Should().BeEquivalentTo(["Allowed"]);
    }

    [Fact]
    public async Task Sorts_returned_actions_by_offset_ascending()
    {
        _modelLoader.ResolveEntityType(Arg.Any<string>()).Returns(CarType);
        _configLoader.GetConfiguration().Returns(new CustomActionsConfiguration
        {
            ["Third"] = NewDefinition("Third", offset: 30),
            ["First"] = NewDefinition("First", offset: 10),
            ["Second"] = NewDefinition("Second", offset: 20),
        });
        _actionResolver.GetRegisteredActionNames().Returns(["First", "Second", "Third"]);
        _permissions.IsAllowedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        var endpoint = NewEndpoint();
        var context = HttpContextWithRouteValues(("objectTypeId", CarType.Id.ToString()));

        var result = await endpoint.HandleAsync(context);
        var body = await ExecuteBodyAsync(result, context);

        using var doc = JsonDocument.Parse(body);
        doc.RootElement.EnumerateArray().Select(e => e.GetProperty("name").GetString())
            .Should().Equal("First", "Second", "Third");
    }

    [Fact]
    public async Task Returned_shape_exposes_display_icon_description_and_flags()
    {
        _modelLoader.ResolveEntityType(Arg.Any<string>()).Returns(CarType);
        var def = new CustomActionDefinition
        {
            DisplayName = TranslatedString.Create("Archive this car"),
            Icon = "archive",
            Description = "Move to archive",
            ShowedOn = "detail",
            SelectionRule = "=1",
            RefreshOnCompleted = true,
            ConfirmationMessageKey = "confirmArchive",
            Offset = 42,
        };
        _configLoader.GetConfiguration().Returns(new CustomActionsConfiguration { ["Archive"] = def });
        _actionResolver.GetRegisteredActionNames().Returns(["Archive"]);
        _permissions.IsAllowedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        var endpoint = NewEndpoint();
        var context = HttpContextWithRouteValues(("objectTypeId", CarType.Id.ToString()));

        var result = await endpoint.HandleAsync(context);
        var body = await ExecuteBodyAsync(result, context);

        using var doc = JsonDocument.Parse(body);
        var first = doc.RootElement[0];
        first.GetProperty("name").GetString().Should().Be("Archive");
        first.GetProperty("icon").GetString().Should().Be("archive");
        first.GetProperty("description").GetString().Should().Be("Move to archive");
        first.GetProperty("showedOn").GetString().Should().Be("detail");
        first.GetProperty("selectionRule").GetString().Should().Be("=1");
        first.GetProperty("refreshOnCompleted").GetBoolean().Should().BeTrue();
        first.GetProperty("confirmationMessageKey").GetString().Should().Be("confirmArchive");
        first.GetProperty("offset").GetInt32().Should().Be(42);
    }

    private ListCustomActions NewEndpoint() =>
        new(_modelLoader, _configLoader, _actionResolver, _permissions);

    private static CustomActionDefinition NewDefinition(string name, int offset) => new()
    {
        DisplayName = TranslatedString.Create(name),
        Offset = offset,
    };

    private static DefaultHttpContext HttpContextWithRouteValues(params (string Key, string Value)[] values)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        foreach (var (k, v) in values) context.Request.RouteValues[k] = v;
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
