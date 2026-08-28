using MintPlayer.Spark.Tests._Infrastructure;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
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
using MintPlayer.Spark.Models;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Endpoints.Actions;

public class ExecuteCustomActionTests
{
    private readonly IModelLoader _modelLoader = Substitute.For<IModelLoader>();
    private readonly ICustomActionResolver _actionResolver = Substitute.For<ICustomActionResolver>();
    private readonly IPermissionService _permissions = Substitute.For<IPermissionService>();
    private readonly IDatabaseAccess _databaseAccess = Substitute.For<IDatabaseAccess>();
    private readonly ICustomActionsConfigurationLoader _configLoader = Substitute.For<ICustomActionsConfigurationLoader>();
    private readonly ClientAccessor _sharedClientAccessor = new();
    private readonly RetryAccessor _retryAccessor;

    public ExecuteCustomActionTests()
    {
        _retryAccessor = new RetryAccessor(_sharedClientAccessor);
        // Default: the action is declared in customActions.json (the M3 config gate). Tests that
        // probe the gate itself override this.
        _configLoader.GetConfiguration().Returns(new CustomActionsConfiguration
        {
            ["Archive"] = new() { DisplayName = new TranslatedString { Translations = new() { ["en"] = "Archive" } } },
        });
    }

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
        // Authenticated on purpose: M-3 makes an unknown type answer exactly as a DENIED one, and
        // for an anonymous caller that is 401. The 404 this asserts is the authenticated shape,
        // which is what makes "no such type" indistinguishable from "not yours".
        var context = NewContext(objectTypeId: "unknown", actionName: "Archive", authenticated: true);

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
    // Changed by audit M-3: an authenticated caller who is denied must be indistinguishable
    // from one asking for something that does not exist, or the status tells them the
    // resource is real. Anonymous callers still get 401 -- see the tests below, and the
    // login redirect depends on it.
    public async Task Authorization_check_failure_for_authenticated_user_returns_404()
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

        (await ExecuteStatusAsync(result, context)).Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_action_absent_from_customActions_json_is_404_even_if_a_class_exists()
    {
        // Security sweep M3: an ICustomAction present in a loaded assembly but not declared in
        // customActions.json must not be executable — execution agrees with the listing.
        var action = Substitute.For<ICustomAction>();
        _modelLoader.ResolveEntityType(Arg.Any<string>()).Returns(CarType);
        _actionResolver.Resolve("Archive").Returns(action);
        _configLoader.GetConfiguration().Returns(new CustomActionsConfiguration()); // empty config

        var endpoint = NewEndpoint();
        var context = NewContext(CarType.Id.ToString(), "Archive", body: new CustomActionRequest());

        var result = await endpoint.HandleAsync(context);

        (await ExecuteStatusAsync(result, context)).Should().Be(HttpStatusCode.NotFound);
        await action.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
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
    public async Task Happy_path_invokes_ExecuteAsync_with_server_loaded_row_checked_entities()
    {
        var action = Substitute.For<ICustomAction>();
        var parent = new MintPlayer.Spark.Abstractions.PersistentObject { Id = "cars/1", Name = "Alice's car (as submitted)", ObjectTypeId = CarType.Id, Attributes = [] };
        string[] selectedIds = ["cars/2"];

        // What the row-gated read path returns is what the action must receive — not the wire POs.
        var serverParent = new MintPlayer.Spark.Abstractions.PersistentObject { Id = "cars/1", Name = "Alice's car (server state)", ObjectTypeId = CarType.Id, Attributes = [] };
        var serverSelected = new MintPlayer.Spark.Abstractions.PersistentObject { Id = "cars/2", Name = "Second car (server state)", ObjectTypeId = CarType.Id, Attributes = [] };
        _databaseAccess.GetPersistentObjectAsync(CarType.Id, "cars/1").Returns(serverParent);
        // The selection is resolved in one batched pass (#327 M2); the parent keeps its own load.
        _databaseAccess.GetPersistentObjectsByIdAsync(CarType.Id, Arg.Any<IReadOnlyList<string>>())
            .Returns(new List<MintPlayer.Spark.Abstractions.PersistentObject> { serverSelected });

        _modelLoader.ResolveEntityType(Arg.Any<string>()).Returns(CarType);
        _actionResolver.Resolve("Archive").Returns(action);

        var endpoint = NewEndpoint();
        var context = NewContext(
            CarType.Id.ToString(),
            "Archive",
            body: new CustomActionRequest { Parent = parent, SelectedItemIds = selectedIds });

        var result = await endpoint.HandleAsync(context);

        await action.Received(1).ExecuteAsync(
            Arg.Is<CustomActionArgs>(a =>
                a.Parent != null && a.Parent.Name == "Alice's car (server state)" &&
                a.SelectedItems.Length == 1 && a.SelectedItems[0].Name == "Second car (server state)" &&
                a.SubmittedParent != null && a.SubmittedParent.Name == "Alice's car (as submitted)" &&
                a.SubmittedSelectedItemIds.Length == 1),
            Arg.Any<CancellationToken>());
        (await ExecuteStatusAsync(result, context)).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_parent_the_row_gate_refuses_is_a_404_not_an_invocation()
    {
        var action = Substitute.For<ICustomAction>();
        var parent = new MintPlayer.Spark.Abstractions.PersistentObject { Id = "cars/999", Name = "Someone else's", ObjectTypeId = CarType.Id, Attributes = [] };
        // Row-gated load returns null for both "missing" and "not yours" — indistinguishable (M-3).
        _databaseAccess.GetPersistentObjectAsync(CarType.Id, "cars/999")
            .Returns((MintPlayer.Spark.Abstractions.PersistentObject?)null);

        _modelLoader.ResolveEntityType(Arg.Any<string>()).Returns(CarType);
        _actionResolver.Resolve("Archive").Returns(action);

        var endpoint = NewEndpoint();
        var context = NewContext(CarType.Id.ToString(), "Archive",
            body: new CustomActionRequest { Parent = parent }, authenticated: true);

        var result = await endpoint.HandleAsync(context);

        (await ExecuteStatusAsync(result, context)).Should().Be(HttpStatusCode.NotFound,
            "holding the type-level action right must not let a caller point the action at any id");
        await action.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
    }

    [Fact]
    public async Task The_parent_is_loaded_under_the_route_type_not_the_client_supplied_type()
    {
        // Security sweep C3: the wire's Parent.ObjectTypeId is untrusted. Loading the parent under
        // it would gate the action against a different (possibly rule-free) type and, combined with
        // the id-to-type binding gap, smuggle a foreign document past every row rule. The load MUST
        // use the route's entity type (CarType), exactly as SelectedItems does.
        var action = Substitute.For<ICustomAction>();
        var foreignType = Guid.NewGuid();
        var parent = new MintPlayer.Spark.Abstractions.PersistentObject
        {
            Id = "cars/1", Name = "x", ObjectTypeId = foreignType, Attributes = [],
        };
        var serverParent = new MintPlayer.Spark.Abstractions.PersistentObject
        {
            Id = "cars/1", Name = "server", ObjectTypeId = CarType.Id, Attributes = [],
        };
        // Only the route-type load is stubbed; a load under the client-chosen type returns null.
        _databaseAccess.GetPersistentObjectAsync(CarType.Id, "cars/1").Returns(serverParent);

        _modelLoader.ResolveEntityType(Arg.Any<string>()).Returns(CarType);
        _actionResolver.Resolve("Archive").Returns(action);

        var endpoint = NewEndpoint();
        var context = NewContext(CarType.Id.ToString(), "Archive",
            body: new CustomActionRequest { Parent = parent });

        var result = await endpoint.HandleAsync(context);

        (await ExecuteStatusAsync(result, context)).Should().Be(HttpStatusCode.OK);
        await _databaseAccess.Received(1).GetPersistentObjectAsync(CarType.Id, "cars/1");
        await _databaseAccess.DidNotReceive().GetPersistentObjectAsync(foreignType, Arg.Any<string>());
        await action.Received(1).ExecuteAsync(
            Arg.Is<CustomActionArgs>(a => a.Parent != null && a.Parent.Name == "server"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_selected_item_the_row_gate_refuses_is_a_404_not_an_invocation()
    {
        var action = Substitute.For<ICustomAction>();
        string[] selectedIds = ["cars/2", "cars/999"];
        // The batch omits a refused id rather than returning null for it — "missing", "foreign
        // collection" and "not yours" are deliberately indistinguishable. Two ids in, one row out:
        // the endpoint must refuse the whole request rather than act on the survivor.
        _databaseAccess.GetPersistentObjectsByIdAsync(CarType.Id, Arg.Any<IReadOnlyList<string>>())
            .Returns(new List<MintPlayer.Spark.Abstractions.PersistentObject>
            {
                new() { Id = "cars/2", Name = "Mine", ObjectTypeId = CarType.Id, Attributes = [] },
            });

        _modelLoader.ResolveEntityType(Arg.Any<string>()).Returns(CarType);
        _actionResolver.Resolve("Archive").Returns(action);

        var endpoint = NewEndpoint();
        var context = NewContext(CarType.Id.ToString(), "Archive",
            body: new CustomActionRequest { SelectedItemIds = selectedIds }, authenticated: true);

        var result = await endpoint.HandleAsync(context);

        (await ExecuteStatusAsync(result, context)).Should().Be(HttpStatusCode.NotFound);
        await action.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
    }

    [Fact]
    public async Task An_id_less_parent_is_not_resolved_but_stays_available_as_submitted_values()
    {
        var action = Substitute.For<ICustomAction>();
        var unsaved = new MintPlayer.Spark.Abstractions.PersistentObject { Id = null, Name = "Unsaved form state", ObjectTypeId = CarType.Id, Attributes = [] };

        _modelLoader.ResolveEntityType(Arg.Any<string>()).Returns(CarType);
        _actionResolver.Resolve("Archive").Returns(action);

        var endpoint = NewEndpoint();
        var context = NewContext(CarType.Id.ToString(), "Archive",
            body: new CustomActionRequest { Parent = unsaved });

        var result = await endpoint.HandleAsync(context);

        await action.Received(1).ExecuteAsync(
            Arg.Is<CustomActionArgs>(a =>
                a.Parent == null &&
                a.SubmittedParent != null && a.SubmittedParent.Name == "Unsaved form state"),
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
        // Envelope shape: { result, operations: [{ type: "retry", step, title, options, ... }] }
        var retry = doc.RootElement.GetProperty("operations").EnumerateArray()
            .First(o => o.GetProperty("type").GetString() == "retry");
        retry.GetProperty("step").GetInt32().Should().Be(2);
        retry.GetProperty("title").GetString().Should().Be("Confirm?");
        retry.GetProperty("options").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("Yes", "No");
        retry.GetProperty("defaultOption").GetString().Should().Be("No");
        retry.GetProperty("message").GetString().Should().Be("Are you sure?");
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
    public async Task Generic_exception_thrown_by_action_returns_500_with_generic_error_message()
    {
        // R2-M1: ex.Message used to flow to the response body. RavenDB-internal
        // strings, index names, etc. leaked. Server logs full detail; client
        // sees "Operation failed".
        var action = Substitute.For<ICustomAction>();
        action.When(a => a.ExecuteAsync(Arg.Any<CustomActionArgs>(), Arg.Any<CancellationToken>()))
            .Do(_ => throw new InvalidOperationException("Raven/Index/Cars/ByCreatedBy not found"));

        _modelLoader.ResolveEntityType(Arg.Any<string>()).Returns(CarType);
        _actionResolver.Resolve("Archive").Returns(action);

        var endpoint = NewEndpoint();
        var context = NewContext(CarType.Id.ToString(), "Archive", body: new CustomActionRequest());

        var result = await endpoint.HandleAsync(context);
        var body = await ExecuteBodyAsync(result, context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        using var doc = JsonDocument.Parse(body);
        var error = doc.RootElement.GetProperty("result").GetProperty("error").GetString();
        error.Should().Be("Operation failed",
            "R2-M1: server-side ex.Message must not flow to the public response body");
        body.Should().NotContain("Raven",
            "Raven-internal strings must not leak to the client");
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

    // ---- Selection batching (#327 M2) ---------------------------------------------------------

    private static MintPlayer.Spark.Abstractions.PersistentObject Row(string id) =>
        new() { Id = id, Name = id, ObjectTypeId = CarType.Id, Attributes = [] };

    [Fact]
    public async Task The_whole_selection_is_resolved_in_one_batched_call()
    {
        // The invariant that replaced the N+1: one call carrying every id, in submitted order —
        // not one call per id behind a lifted request ceiling.
        var action = Substitute.For<ICustomAction>();
        string[] selectedIds = ["cars/1", "cars/2", "cars/3"];

        _databaseAccess.GetPersistentObjectsByIdAsync(CarType.Id, Arg.Any<IReadOnlyList<string>>())
            .Returns(new List<MintPlayer.Spark.Abstractions.PersistentObject>
            {
                Row("cars/1"), Row("cars/2"), Row("cars/3"),
            });

        _modelLoader.ResolveEntityType(Arg.Any<string>()).Returns(CarType);
        _actionResolver.Resolve("Archive").Returns(action);

        var context = NewContext(CarType.Id.ToString(), "Archive",
            body: new CustomActionRequest { SelectedItemIds = selectedIds });

        var result = await NewEndpoint().HandleAsync(context);

        (await ExecuteStatusAsync(result, context)).Should().Be(HttpStatusCode.OK);
        await _databaseAccess.Received(1).GetPersistentObjectsByIdAsync(
            CarType.Id,
            Arg.Is<IReadOnlyList<string>>(ids =>
                ids.Count == 3 && ids[0] == "cars/1" && ids[1] == "cars/2" && ids[2] == "cars/3"));
        await _databaseAccess.DidNotReceive().GetPersistentObjectAsync(CarType.Id, Arg.Any<string>());
    }

    [Fact]
    public async Task A_selection_the_batch_shrinks_is_refused_rather_than_partially_applied()
    {
        // The improvement over the prior art, which drops unresolvable rows with no exception and
        // no count check — so a bulk action there can act on 498 of 500 rows and report success.
        var action = Substitute.For<ICustomAction>();
        string[] selectedIds = ["cars/1", "cars/2", "cars/3"];

        _databaseAccess.GetPersistentObjectsByIdAsync(CarType.Id, Arg.Any<IReadOnlyList<string>>())
            .Returns(new List<MintPlayer.Spark.Abstractions.PersistentObject> { Row("cars/1"), Row("cars/3") });

        _modelLoader.ResolveEntityType(Arg.Any<string>()).Returns(CarType);
        _actionResolver.Resolve("Archive").Returns(action);

        var context = NewContext(CarType.Id.ToString(), "Archive",
            body: new CustomActionRequest { SelectedItemIds = selectedIds }, authenticated: true);

        var result = await NewEndpoint().HandleAsync(context);

        (await ExecuteStatusAsync(result, context)).Should().Be(HttpStatusCode.NotFound);
        await action.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
    }

    [Fact]
    public async Task A_repeated_id_in_the_selection_is_not_a_refusal()
    {
        // The batch collapses duplicates, so the short-result check compares against the DISTINCT
        // count — otherwise selecting the same row twice would 404 the request.
        var action = Substitute.For<ICustomAction>();
        string[] selectedIds = ["cars/1", "cars/1"];

        _databaseAccess.GetPersistentObjectsByIdAsync(CarType.Id, Arg.Any<IReadOnlyList<string>>())
            .Returns(new List<MintPlayer.Spark.Abstractions.PersistentObject> { Row("cars/1") });

        _modelLoader.ResolveEntityType(Arg.Any<string>()).Returns(CarType);
        _actionResolver.Resolve("Archive").Returns(action);

        var context = NewContext(CarType.Id.ToString(), "Archive",
            body: new CustomActionRequest { SelectedItemIds = selectedIds });

        var result = await NewEndpoint().HandleAsync(context);

        (await ExecuteStatusAsync(result, context)).Should().Be(HttpStatusCode.OK);
        await action.Received(1).ExecuteAsync(
            Arg.Is<CustomActionArgs>(a => a.SelectedItems.Length == 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_id_less_selected_item_refuses_the_whole_request()
    {
        // An id-less selected item names no row and cannot be verified. Unlike an id-less PARENT
        // (which stays available as submitted values), it fails the request — batching must not
        // quietly turn it into a dropped element.
        var action = Substitute.For<ICustomAction>();
        string[] selectedIds = ["cars/1", ""];

        _modelLoader.ResolveEntityType(Arg.Any<string>()).Returns(CarType);
        _actionResolver.Resolve("Archive").Returns(action);

        var context = NewContext(CarType.Id.ToString(), "Archive",
            body: new CustomActionRequest { SelectedItemIds = selectedIds }, authenticated: true);

        var result = await NewEndpoint().HandleAsync(context);

        (await ExecuteStatusAsync(result, context)).Should().Be(HttpStatusCode.NotFound);
        await action.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
    }

    [Theory]
    [InlineData(200, HttpStatusCode.OK)]
    [InlineData(201, HttpStatusCode.BadRequest)]
    public async Task The_selection_ceiling_is_enforced_at_two_hundred(int count, HttpStatusCode expected)
    {
        // Untested until #327 M2, which is exactly when it became tempting to raise: batching made
        // the selection one round-trip, but the ceiling bounds materialized WORK, not round-trips.
        var action = Substitute.For<ICustomAction>();
        var selectedIds = Enumerable.Range(1, count).Select(i => $"cars/{i}").ToArray();

        _databaseAccess.GetPersistentObjectsByIdAsync(CarType.Id, Arg.Any<IReadOnlyList<string>>())
            .Returns(call => (IReadOnlyList<MintPlayer.Spark.Abstractions.PersistentObject>)
                [.. call.ArgAt<IReadOnlyList<string>>(1).Select(Row)]);

        _modelLoader.ResolveEntityType(Arg.Any<string>()).Returns(CarType);
        _actionResolver.Resolve("Archive").Returns(action);

        var context = NewContext(CarType.Id.ToString(), "Archive",
            body: new CustomActionRequest { SelectedItemIds = selectedIds });

        var result = await NewEndpoint().HandleAsync(context);

        (await ExecuteStatusAsync(result, context)).Should().Be(expected);
    }

    [Fact]
    public async Task Configure_marks_the_endpoint_with_RequireAntiforgeryTokenAttribute()
    {
        // TestServer instead of Kestrel — the test only inspects EndpointDataSource
        // metadata, no real HTTP needed. Kestrel's default 127.0.0.1:5000 binding
        // collides with parallel test processes / leftover sockets on CI runners.
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
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

    private readonly Raven.Client.Documents.Session.IAsyncDocumentSession _session =
        Substitute.For<Raven.Client.Documents.Session.IAsyncDocumentSession>();

    // Permissive by default: these tests exercise dispatch, not row rules. The row-action gate
    // has its own tests; a Substitute would return false for the unstubbed Task<bool> and refuse
    // every request that names a row.
    private readonly IRowSecurity _rowSecurity = new PermissiveRowSecurity();
    private readonly ISparkTypeResolver _typeResolver = Substitute.For<ISparkTypeResolver>();

    private ExecuteCustomAction NewEndpoint() =>
        new(_modelLoader, _rowSecurity, _typeResolver, _actionResolver, _permissions, _retryAccessor, _sharedClientAccessor, NullLogger<ExecuteCustomAction>.Instance, _databaseAccess, _session, _configLoader);

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
