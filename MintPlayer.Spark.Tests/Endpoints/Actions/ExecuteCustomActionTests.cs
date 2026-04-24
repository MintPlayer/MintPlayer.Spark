using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.Endpoints.Actions;
using MintPlayer.Spark.Exceptions;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Endpoints.Actions;

public class ExecuteCustomActionTests
{
    private readonly IModelLoader _modelLoader = Substitute.For<IModelLoader>();
    private readonly ICustomActionResolver _actionResolver = Substitute.For<ICustomActionResolver>();
    private readonly IPermissionService _permissions = Substitute.For<IPermissionService>();
    private readonly RetryAccessor _retryAccessor = new(new ClientAccessor());

    private static readonly EntityTypeDefinition CarType = new()
    {
        Id = Guid.NewGuid(),
        Name = "Car",
        ClrType = "Fleet.Entities.Car",
    };

    [Fact]
    public async Task Returns_404_when_entity_type_cannot_be_resolved()
    {
        _modelLoader.ResolveEntityType(Arg.Any<string>()).Returns((EntityTypeDefinition?)null);
        var endpoint = NewEndpoint();
        var context = NewContext(objectTypeId: "unknown", actionName: "Archive");

        var result = await endpoint.HandleAsync(context);

        (await ExecuteStatusAsync(result, context)).Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Authorization_check_failure_for_anonymous_user_returns_401()
    {
        _modelLoader.ResolveEntityType(Arg.Any<string>()).Returns(CarType);
        _permissions.EnsureAuthorizedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new SparkAccessDeniedException("denied")));

        var endpoint = NewEndpoint();
        var context = NewContext(objectTypeId: CarType.Id.ToString(), actionName: "Archive");

        var result = await endpoint.HandleAsync(context);

        (await ExecuteStatusAsync(result, context)).Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Authorization_check_failure_for_authenticated_user_returns_403()
    {
        _modelLoader.ResolveEntityType(Arg.Any<string>()).Returns(CarType);
        _permissions.EnsureAuthorizedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new SparkAccessDeniedException("denied")));

        var endpoint = NewEndpoint();
        var context = NewContext(
            objectTypeId: CarType.Id.ToString(),
            actionName: "Archive",
            authenticated: true);

        var result = await endpoint.HandleAsync(context);

        (await ExecuteStatusAsync(result, context)).Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Returns_404_when_the_action_name_does_not_resolve_to_an_implementation()
    {
        _modelLoader.ResolveEntityType(Arg.Any<string>()).Returns(CarType);
        _actionResolver.Resolve("NoSuchAction").Returns((ICustomAction?)null);

        var endpoint = NewEndpoint();
        var context = NewContext(CarType.Id.ToString(), "NoSuchAction");

        var result = await endpoint.HandleAsync(context);

        (await ExecuteStatusAsync(result, context)).Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Happy_path_invokes_ExecuteAsync_with_parent_and_selected_items_from_request_body()
    {
        var action = Substitute.For<ICustomAction>();
        var parent = new MintPlayer.Spark.Abstractions.PersistentObject { Id = "cars/1", Name = "Alice's car", ObjectTypeId = CarType.Id, Attributes = [] };
        var selected = new MintPlayer.Spark.Abstractions.PersistentObject[]
        {
            new() { Id = "cars/2", Name = "Bob's car", ObjectTypeId = CarType.Id, Attributes = [] },
        };

        _modelLoader.ResolveEntityType(Arg.Any<string>()).Returns(CarType);
        _actionResolver.Resolve("Archive").Returns(action);

        var endpoint = NewEndpoint();
        var context = NewContext(
            CarType.Id.ToString(),
            "Archive",
            body: new CustomActionRequest { Parent = parent, SelectedItems = selected });

        var result = await endpoint.HandleAsync(context);

        await action.Received(1).ExecuteAsync(
            Arg.Is<CustomActionArgs>(a =>
                a.Parent != null && a.Parent.Id == "cars/1" &&
                a.SelectedItems.Length == 1 && a.SelectedItems[0].Id == "cars/2"),
            Arg.Any<CancellationToken>());
        (await ExecuteStatusAsync(result, context)).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Empty_body_forwards_null_parent_and_empty_selected_items()
    {
        var action = Substitute.For<ICustomAction>();
        _modelLoader.ResolveEntityType(Arg.Any<string>()).Returns(CarType);
        _actionResolver.Resolve("Archive").Returns(action);

        var endpoint = NewEndpoint();
        var context = NewContext(CarType.Id.ToString(), "Archive", body: new CustomActionRequest());

        var result = await endpoint.HandleAsync(context);

        await action.Received(1).ExecuteAsync(
            Arg.Is<CustomActionArgs>(a => a.Parent == null && a.SelectedItems.Length == 0),
            Arg.Any<CancellationToken>());
        (await ExecuteStatusAsync(result, context)).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SparkRetryActionException_thrown_by_action_returns_449_with_retry_payload()
    {
        var action = Substitute.For<ICustomAction>();
        action.When(a => a.ExecuteAsync(Arg.Any<CustomActionArgs>(), Arg.Any<CancellationToken>()))
            .Do(_ => throw new SparkRetryActionException(
                step: 2, title: "Confirm?", options: ["Yes", "No"],
                defaultOption: "No", persistentObject: null, message: "Are you sure?"));

        _modelLoader.ResolveEntityType(Arg.Any<string>()).Returns(CarType);
        _actionResolver.Resolve("Archive").Returns(action);

        var endpoint = NewEndpoint();
        var context = NewContext(CarType.Id.ToString(), "Archive", body: new CustomActionRequest());

        var result = await endpoint.HandleAsync(context);
        var body = await ExecuteBodyAsync(result, context);

        ((HttpStatusCode)context.Response.StatusCode).Should().Be((HttpStatusCode)449);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("type").GetString().Should().Be("retry-action");
        doc.RootElement.GetProperty("step").GetInt32().Should().Be(2);
        doc.RootElement.GetProperty("title").GetString().Should().Be("Confirm?");
        doc.RootElement.GetProperty("options").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("Yes", "No");
        doc.RootElement.GetProperty("defaultOption").GetString().Should().Be("No");
        doc.RootElement.GetProperty("message").GetString().Should().Be("Are you sure?");
    }

    [Fact]
    public async Task RetryResults_in_the_request_are_forwarded_to_the_RetryAccessor()
    {
        var action = Substitute.For<ICustomAction>();
        _modelLoader.ResolveEntityType(Arg.Any<string>()).Returns(CarType);
        _actionResolver.Resolve("Archive").Returns(action);

        var endpoint = NewEndpoint();
        var context = NewContext(CarType.Id.ToString(), "Archive", body: new CustomActionRequest
        {
            RetryResults = [new RetryResult { Option = "Yes", Step = 0 }, new RetryResult { Option = "Proceed", Step = 1 }],
        });

        await endpoint.HandleAsync(context);

        _retryAccessor.AnsweredResults.Should().NotBeNull();
        _retryAccessor.AnsweredResults!.Should().HaveCount(2);
        _retryAccessor.AnsweredResults[0].Option.Should().Be("Yes");
        _retryAccessor.AnsweredResults[1].Option.Should().Be("Proceed");
    }

    [Fact]
    public async Task Generic_exception_thrown_by_action_returns_500_with_the_message_as_error()
    {
        var action = Substitute.For<ICustomAction>();
        action.When(a => a.ExecuteAsync(Arg.Any<CustomActionArgs>(), Arg.Any<CancellationToken>()))
            .Do(_ => throw new InvalidOperationException("Boom"));

        _modelLoader.ResolveEntityType(Arg.Any<string>()).Returns(CarType);
        _actionResolver.Resolve("Archive").Returns(action);

        var endpoint = NewEndpoint();
        var context = NewContext(CarType.Id.ToString(), "Archive", body: new CustomActionRequest());

        var result = await endpoint.HandleAsync(context);
        var body = await ExecuteBodyAsync(result, context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("error").GetString().Should().Be("Boom");
    }

    [Fact]
    public async Task SparkAccessDeniedException_thrown_inside_action_maps_to_401_for_anonymous_user()
    {
        var action = Substitute.For<ICustomAction>();
        action.When(a => a.ExecuteAsync(Arg.Any<CustomActionArgs>(), Arg.Any<CancellationToken>()))
            .Do(_ => throw new SparkAccessDeniedException("denied inside action"));

        _modelLoader.ResolveEntityType(Arg.Any<string>()).Returns(CarType);
        _actionResolver.Resolve("Archive").Returns(action);

        var endpoint = NewEndpoint();
        var context = NewContext(CarType.Id.ToString(), "Archive", body: new CustomActionRequest());

        var result = await endpoint.HandleAsync(context);

        (await ExecuteStatusAsync(result, context)).Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Configure_marks_the_endpoint_with_RequireAntiforgeryTokenAttribute()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddRouting();
        var app = builder.Build();

        var routeBuilder = app.MapPost("/test-action", () => Results.Ok());
        InvokeConfigure<ExecuteCustomAction>(routeBuilder);

        await app.StartAsync();
        try
        {
            var endpoints = app.Services
                .GetRequiredService<IEnumerable<EndpointDataSource>>()
                .SelectMany(ds => ds.Endpoints);

            endpoints.Any(e => e.Metadata.GetMetadata<RequireAntiforgeryTokenAttribute>() is not null)
                .Should().BeTrue();
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static void InvokeConfigure<TEndpoint>(RouteHandlerBuilder builder)
        where TEndpoint : IEndpointBase
        => TEndpoint.Configure(builder);

    private ExecuteCustomAction NewEndpoint() =>
        new(_modelLoader, _actionResolver, _permissions, _retryAccessor, NullLogger<ExecuteCustomAction>.Instance);

    private static DefaultHttpContext NewContext(
        string objectTypeId,
        string actionName,
        CustomActionRequest? body = null,
        bool authenticated = false)
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.RouteValues["objectTypeId"] = objectTypeId;
        context.Request.RouteValues["actionName"] = actionName;

        if (authenticated)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "alice")], authenticationType: "TestScheme"));
        }

        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var bytes = Encoding.UTF8.GetBytes(json);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = bytes.Length;
        }

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
