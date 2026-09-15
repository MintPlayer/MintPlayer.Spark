using System.Net;
using System.Text;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Client.Tests._Infrastructure;

namespace MintPlayer.Spark.Client.Tests;

/// <summary>
/// M4 — client operations are surfaced, and the one a headless client can act on is applied.
/// </summary>
/// <remarks>
/// <para>
/// Scripted for the same reason M3 is: what M4 gets right or wrong is what it does with a body. The
/// envelope shapes below are the server's real ones — <c>kind</c> is a <b>number</b> because no
/// <c>JsonStringEnumConverter</c> is registered anywhere in the server, and a fixture written with
/// <c>"kind":"Success"</c> would pass against nothing a real host sends.
/// </para>
/// <para>
/// ⚠️ The forward-compatibility tests are the load-bearing ones. Binding the envelope to the
/// server's own <c>ClientOperation</c> DTOs would compile and pass every other test here, and then
/// fail the <b>entire response</b> the first time a newer server emits an operation type this client
/// has never seen — the opposite of what the contract promises.
/// </para>
/// </remarks>
public class ClientOperationTests
{
    private static (SparkClient client, ScriptedHttpHandler handler) NewClientWithWarmup()
    {
        var handler = new ScriptedHttpHandler()
            .EnqueueWithCookies(
                ".AspNetCore.Antiforgery.abc=val; Path=/",
                "XSRF-TOKEN=token; Path=/");
        return (NewClient(handler), handler);
    }

    private static SparkClient NewClient(ScriptedHttpHandler handler)
        => new(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, ownsClient: true);

    private static HttpResponseMessage Envelope(string resultJson, string operationsJson, HttpStatusCode status = HttpStatusCode.OK)
        => new(status)
        {
            Content = new StringContent(
                $$"""{"result":{{resultJson}},"operations":[{{operationsJson}}]}""",
                Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage Prompt(string operationsJson)
        => Envelope("null", operationsJson, (HttpStatusCode)449);

    private const string Retry =
        """{"type":"retry","step":0,"title":"Are you sure?","options":["Yes"],"defaultOption":null,"message":null,"persistentObject":null}""";

    private static PersistentObject Car(Guid typeId, string id, params PersistentObjectAttribute[] attributes)
        => new() { Id = id, Name = "Car", ObjectTypeId = typeId, Attributes = attributes };

    // ------------------------------------------------------------------------------------------
    // FR8 — operations are surfaced
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_notify_operation_is_surfaced_on_the_result()
    {
        var (client, handler) = NewClientWithWarmup();
        handler.Enqueue(Envelope("null", """{"type":"notify","message":"Saved","kind":1,"durationMs":3000}"""));

        using (client)
        {
            var result = await client.ExecuteActionAsync(Guid.NewGuid(), "Save");

            var notify = result.Operations.OfType<SparkNotifyOperation>().Single();
            notify.Message.Should().Be("Saved");
            notify.Kind.Should().Be(NotificationKind.Success);
            notify.DurationMs.Should().Be(3000);
        }
    }

    [Fact]
    public async Task Operations_are_surfaced_in_emission_order()
    {
        var (client, handler) = NewClientWithWarmup();
        handler.Enqueue(Envelope("null",
            """{"type":"notify","message":"first","kind":0},"""
            + """{"type":"refreshQuery","queryId":"cars"},"""
            + """{"type":"notify","message":"second","kind":0}"""));

        using (client)
        {
            var result = await client.ExecuteActionAsync(Guid.NewGuid(), "Sync");

            result.Operations.Select(o => o.Type).Should()
                .BeEquivalentTo(["notify", "refreshQuery", "notify"], o => o.WithStrictOrdering());
        }
    }

    [Fact]
    public async Task A_notify_from_a_save_reaches_the_per_call_handler()
    {
        var (client, handler) = NewClientWithWarmup();
        var typeId = Guid.NewGuid();
        handler.Enqueue(Envelope(
            $$"""{"id":"cars/1","name":"Car","objectTypeId":"{{typeId}}","attributes":[]}""",
            """{"type":"notify","message":"Saved 3 rows","kind":1}"""));

        var seen = new List<SparkClientOperation>();
        using (client)
        {
            // A save returns the object, so the sink is the only channel an operation has. That is
            // the whole reason SparkOperationHandler exists rather than a richer return type.
            await client.UpdatePersistentObjectAsync(
                Car(typeId, "cars/1"), onOperation: op => seen.Add(op));
        }

        seen.OfType<SparkNotifyOperation>().Single().Message.Should().Be("Saved 3 rows");
    }

    [Fact]
    public async Task The_client_level_handler_catches_calls_that_pass_none()
    {
        var (client, handler) = NewClientWithWarmup();
        handler.Enqueue(Envelope("null", """{"type":"notify","message":"done","kind":0}"""));

        var seen = new List<SparkClientOperation>();
        using (client)
        {
            client.OperationHandler = op => seen.Add(op);
            await client.ExecuteActionAsync(Guid.NewGuid(), "Sync");
        }

        seen.Should().ContainSingle();
    }

    // ------------------------------------------------------------------------------------------
    // FR10 — an unknown operation is ignored, never thrown on
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_unknown_operation_type_is_ignored_not_thrown()
    {
        var (client, handler) = NewClientWithWarmup();
        handler.Enqueue(Envelope("null",
            """{"type":"teleport","destination":"mars","payload":{"nested":true}},"""
            + """{"type":"notify","message":"still here","kind":0}"""));

        using (client)
        {
            var result = await client.ExecuteActionAsync(Guid.NewGuid(), "Sync");

            // The known one still arrives — the unknown one did not take the response down with it.
            result.Operations.OfType<SparkNotifyOperation>().Single().Message.Should().Be("still here");

            var unknown = result.Operations.OfType<SparkUnknownOperation>().Single();
            unknown.Type.Should().Be("teleport");
            unknown.Raw.GetProperty("destination").GetString().Should().Be("mars");
        }
    }

    [Fact]
    public async Task An_operation_missing_a_required_field_does_not_throw()
    {
        var (client, handler) = NewClientWithWarmup();
        // Every server-side operation DTO declares its properties `required`, so a truncated payload
        // is exactly as fatal as an unknown type if the envelope is bound rather than read.
        handler.Enqueue(Envelope("null", """{"type":"refreshAttribute","id":"cars/1"}"""));

        using (client)
        {
            var result = await client.ExecuteActionAsync(Guid.NewGuid(), "Sync");

            var refresh = result.Operations.OfType<SparkRefreshAttributeOperation>().Single();
            refresh.Id.Should().Be("cars/1");
            refresh.ObjectTypeId.Should().BeNull();
            refresh.AttributeName.Should().BeNull();
        }
    }

    [Fact]
    public async Task A_body_that_is_not_an_envelope_surfaces_no_operations()
    {
        var (client, handler) = NewClientWithWarmup();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not json at all", Encoding.UTF8, "text/plain"),
        });

        using (client)
        {
            var result = await client.ExecuteActionAsync(Guid.NewGuid(), "Sync");
            result.Operations.Should().BeEmpty();
        }
    }

    // ------------------------------------------------------------------------------------------
    // FR11 — non-retry operations are observable before the prompt
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Non_retry_operations_are_surfaced_before_the_prompt_is_answered()
    {
        var (client, handler) = NewClientWithWarmup();
        handler.Enqueue(Prompt("""{"type":"notify","message":"Saved 3 of 4","kind":2},""" + Retry));
        handler.Enqueue(Envelope("null", ""));

        var order = new List<string>();
        using (client)
        {
            await client.ExecuteActionAsync(
                Guid.NewGuid(), "Sync",
                onRetry: (prompt, _) =>
                {
                    order.Add($"prompt:{prompt.Title}");
                    return Task.FromResult<RetryAnswer?>(RetryAnswer.Choose("Yes"));
                },
                onOperation: op => order.Add($"operation:{op.Type}"));
        }

        // The notify explains the question. Delivering it afterwards would show the user a
        // confirmation dialog first and the reason for it second.
        order.Should().BeEquivalentTo(["operation:notify", "prompt:Are you sure?"], o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task Non_retry_operations_survive_an_unanswered_prompt()
    {
        var (client, handler) = NewClientWithWarmup();
        handler.Enqueue(Prompt("""{"type":"notify","message":"Saved 3 of 4","kind":2},""" + Retry));

        using (client)
        {
            var thrown = await Assert.ThrowsAsync<SparkRetryRequiredException>(
                () => client.UpdatePersistentObjectAsync(Car(Guid.NewGuid(), "cars/1")));

            thrown.Operations.OfType<SparkNotifyOperation>().Single().Message.Should().Be("Saved 3 of 4");
            thrown.Operations.Should().NotContain(o => o is SparkRetryOperation);
        }
    }

    [Fact]
    public async Task A_prompt_returned_as_a_result_carries_the_operations_that_preceded_it()
    {
        var (client, handler) = NewClientWithWarmup();
        handler.Enqueue(Prompt("""{"type":"notify","message":"Two rows are locked","kind":2},""" + Retry));

        using (client)
        {
            // No handler: the prompt comes back as a result, and a caller deciding how to answer it
            // needs what the hook said first.
            var result = await client.ExecuteActionAsync(Guid.NewGuid(), "Sync");

            result.IsRetry.Should().BeTrue();
            result.Operations.OfType<SparkNotifyOperation>().Single().Message.Should().Be("Two rows are locked");
            result.Operations.Should().NotContain(o => o is SparkRetryOperation);
        }
    }

    // ------------------------------------------------------------------------------------------
    // FR9 — refreshAttribute is applied
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void A_refreshAttribute_patches_a_scalar_attribute_in_place()
    {
        var typeId = Guid.NewGuid();
        var po = Car(typeId, "cars/1", new PersistentObjectAttribute { Name = "Status", Value = "Available" });

        var applied = SparkClientOperations.Apply(po, SparkClientOperations.Parse(
            $$"""{"operations":[{"type":"refreshAttribute","objectTypeId":"{{typeId}}","id":"cars/1","attributeName":"Status","value":"Stolen"}]}"""));

        applied.Should().Be(1);
        po["Status"].Value!.ToString().Should().Be("Stolen");
    }

    [Fact]
    public void A_refreshAttribute_for_a_different_object_is_not_applied()
    {
        var typeId = Guid.NewGuid();
        var po = Car(typeId, "cars/1", new PersistentObjectAttribute { Name = "Status", Value = "Available" });

        // Same id, different type. Ids are not unique across types, and matching on id alone
        // patches the wrong object in exactly the case nobody is watching for.
        var applied = SparkClientOperations.Apply(po, SparkClientOperations.Parse(
            $$"""{"operations":[{"type":"refreshAttribute","objectTypeId":"{{Guid.NewGuid()}}","id":"cars/1","attributeName":"Status","value":"Stolen"}]}"""));

        applied.Should().Be(0);
        po["Status"].Value!.ToString().Should().Be("Available");
    }

    [Fact]
    public void A_refreshAttribute_for_an_absent_attribute_is_dropped_silently()
    {
        var typeId = Guid.NewGuid();
        var po = Car(typeId, "cars/1", new PersistentObjectAttribute { Name = "Status", Value = "Available" });

        // ⚠️ The PersistentObject indexer THROWS on an unknown name. A patch naming an attribute
        // this object does not carry is an ordinary outcome, not an error.
        var applied = SparkClientOperations.Apply(po, SparkClientOperations.Parse(
            $$"""{"operations":[{"type":"refreshAttribute","objectTypeId":"{{typeId}}","id":"cars/1","attributeName":"Mileage","value":"42"}]}"""));

        applied.Should().Be(0);
    }

    [Fact]
    public void A_refreshAttribute_replaces_an_AsDetail_grids_rows_from_objects()
    {
        var typeId = Guid.NewGuid();
        var detail = new PersistentObjectAttributeAsDetail
        {
            Name = "Services",
            DataType = "AsDetail",
            IsArray = true,
            Objects = [],
        };
        var po = Car(typeId, "cars/1", detail);

        // The headline case: value is null for an AsDetail attribute and the rows travel in
        // `objects`, so a patch that could only carry `value` was structurally unable to refresh a
        // detail grid — and failed silently, because null over null repaints nothing.
        var applied = SparkClientOperations.Apply(po, SparkClientOperations.Parse(
            $$"""
            {"operations":[{"type":"refreshAttribute","objectTypeId":"{{typeId}}","id":"cars/1",
              "attributeName":"Services","value":null,"object":null,
              "objects":[{"id":"services/1","name":"Service","objectTypeId":"{{Guid.Empty}}","attributes":[]}]}]}
            """));

        applied.Should().Be(1);
        detail.Objects.Should().ContainSingle();
        detail.Objects!.Single().Id.Should().Be("services/1");
    }

    [Fact]
    public void An_empty_objects_array_empties_an_AsDetail_grid()
    {
        var typeId = Guid.NewGuid();
        var detail = new PersistentObjectAttributeAsDetail
        {
            Name = "Services",
            DataType = "AsDetail",
            IsArray = true,
            Objects = [Car(Guid.Empty, "services/1")],
        };
        var po = Car(typeId, "cars/1", detail);

        // An empty array is an instruction — "the action emptied this grid" — and only its absence
        // means "leave the rows alone".
        var applied = SparkClientOperations.Apply(po, SparkClientOperations.Parse(
            $$"""{"operations":[{"type":"refreshAttribute","objectTypeId":"{{typeId}}","id":"cars/1","attributeName":"Services","objects":[]}]}"""));

        applied.Should().Be(1);
        detail.Objects.Should().BeEmpty();
    }

    [Fact]
    public void Object_and_objects_are_ignored_on_a_scalar_attribute()
    {
        var typeId = Guid.NewGuid();
        var scalar = new PersistentObjectAttribute { Name = "Status", Value = "Available" };
        var po = Car(typeId, "cars/1", scalar);

        // The server writes object/objects as null on every scalar patch. Applying them
        // unconditionally would report a change on every patch — and on the frontend, repaint.
        var applied = SparkClientOperations.Apply(po, SparkClientOperations.Parse(
            $$"""{"operations":[{"type":"refreshAttribute","objectTypeId":"{{typeId}}","id":"cars/1","attributeName":"Status","object":null,"objects":null}]}"""));

        applied.Should().Be(0);
        scalar.Value!.ToString().Should().Be("Available");
    }

    [Fact]
    public void An_absent_value_leaves_the_attribute_alone()
    {
        var typeId = Guid.NewGuid();
        var scalar = new PersistentObjectAttribute { Name = "Status", Value = "Available" };
        var po = Car(typeId, "cars/1", scalar);

        var applied = SparkClientOperations.Apply(po, SparkClientOperations.Parse(
            $$"""{"operations":[{"type":"refreshAttribute","objectTypeId":"{{typeId}}","id":"cars/1","attributeName":"Status"}]}"""));

        applied.Should().Be(0);
        scalar.Value!.ToString().Should().Be("Available");
    }

    // ------------------------------------------------------------------------------------------
    // Surface-only operations
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void A_disableAction_operation_is_surfaced_and_does_nothing()
    {
        // Pinned so that nobody later implements it here: disableAction is a no-op in the browser
        // too. Note also that DisableActionsOn(po, "A", "B") emits one operation per name.
        var operations = SparkClientOperations.Parse(
            """
            {"operations":[
              {"type":"disableAction","actionName":"Delete","target":{"kind":"persistentObject","objectTypeId":"x","id":"cars/1"}},
              {"type":"disableAction","actionName":"Edit","target":{"kind":"persistentObject","objectTypeId":"x","id":"cars/1"}}]}
            """);

        operations.OfType<SparkDisableActionOperation>().Select(o => o.ActionName)
            .Should().BeEquivalentTo(["Delete", "Edit"]);
        operations.OfType<SparkDisableActionOperation>().First().TargetKind.Should().Be("persistentObject");

        var po = Car(Guid.NewGuid(), "cars/1");
        SparkClientOperations.Apply(po, operations).Should().Be(0);
    }

    [Fact]
    public void A_navigate_operation_is_surfaced_and_does_nothing()
    {
        var operations = SparkClientOperations.Parse(
            """{"operations":[{"type":"navigate","objectTypeId":"x","id":"cars/1","routeName":null}]}""");

        var navigate = operations.OfType<SparkNavigateOperation>().Single();
        navigate.Id.Should().Be("cars/1");
        navigate.RouteName.Should().BeNull();
    }
}
