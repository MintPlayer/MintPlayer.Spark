using System.Net;
using System.Text;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Client.Tests._Infrastructure;

namespace MintPlayer.Spark.Client.Tests;

/// <summary>
/// M5 / M6 — one wire fact per endpoint the client gained, and the two viewer headers.
/// </summary>
/// <remarks>
/// <para>
/// What M5 can get wrong is the wire: a path that moved, a parameter that used to be a route
/// segment and is now a body field, an envelope read where there is none. All of that is visible in
/// a scripted request and none of it is visible in a green integration test against a stub.
/// </para>
/// <para>
/// ⚠️ The route table is literal for <c>/po</c>, <c>/queries</c> and <c>/actions</c> — but
/// <b>not</b> for the endpoints here. <c>lookupref/{name}</c>, <c>lookupref/{name}/{key}</c> and
/// <c>types/{id}</c> still carry variables, and a lookup-reference key is user data, so the
/// escaping tests below are the point rather than decoration.
/// </para>
/// </remarks>
public class EndpointCoverageTests
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

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Envelope(string resultJson)
        => Json($$"""{"result":{{resultJson}},"operations":[]}""");

    private static string PoJson(Guid typeId, string id)
        => $$"""{"id":"{{id}}","name":"Car","objectTypeId":"{{typeId}}","attributes":[]}""";

    // ------------------------------------------------------------------------------------------
    // The three enveloped, retry-capable verbs
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Refresh_posts_the_object_and_what_triggered_it()
    {
        var (client, handler) = NewClientWithWarmup();
        var typeId = Guid.NewGuid();
        handler.Enqueue(Envelope(PoJson(typeId, "cars/1")));

        using (client)
        {
            var refreshed = await client.RefreshPersistentObjectAsync(
                new PersistentObject { Id = "cars/1", Name = "Car", ObjectTypeId = typeId, Attributes = [] },
                triggeredBy: "Brand");

            refreshed.Id.Should().Be("cars/1");
        }

        handler.Requests[^1].RequestUri!.AbsolutePath.Should().Be("/spark/po/refresh");
        handler.Requests[^1].Method.Should().Be(HttpMethod.Post);

        var body = handler.LastBody();
        body.GetProperty("objectTypeId").GetString().Should().Be(typeId.ToString());
        body.GetProperty("triggeredBy").GetString().Should().Be("Brand");
        body.GetProperty("persistentObject").GetProperty("id").GetString().Should().Be("cars/1");
    }

    [Fact]
    public async Task New_names_the_detail_attribute_and_its_owner()
    {
        var (client, handler) = NewClientWithWarmup();
        var typeId = Guid.NewGuid();
        handler.Enqueue(Envelope(PoJson(typeId, "")));

        using (client)
        {
            await client.NewPersistentObjectAsync(
                typeId, asDetailAttribute: "Services", parentType: "Car", parentId: "cars/1",
                parameters: new Dictionary<string, string> { ["mode"] = "quick" });
        }

        handler.Requests[^1].RequestUri!.AbsolutePath.Should().Be("/spark/po/new");
        var body = handler.LastBody();
        body.GetProperty("asDetailAttribute").GetString().Should().Be("Services");
        body.GetProperty("parentType").GetString().Should().Be("Car");
        body.GetProperty("parentId").GetString().Should().Be("cars/1");
        body.GetProperty("parameters").GetProperty("mode").GetString().Should().Be("quick");
    }

    [Fact]
    public async Task DeleteRow_names_the_row_within_its_owner()
    {
        var (client, handler) = NewClientWithWarmup();
        handler.Enqueue(Envelope("""{"removed":true}"""));

        var typeId = Guid.NewGuid();
        using (client)
        {
            await client.DeleteRowAsync(typeId, "Services", "Car", "cars/1", rowKey: "svc-3");
        }

        handler.Requests[^1].RequestUri!.AbsolutePath.Should().Be("/spark/po/delete-row");
        handler.LastBody().GetProperty("rowKey").GetString().Should().Be("svc-3");
    }

    [Fact]
    public async Task A_refresh_can_be_asked_a_question()
    {
        var (client, handler) = NewClientWithWarmup();
        var typeId = Guid.NewGuid();
        handler.Enqueue(new HttpResponseMessage((HttpStatusCode)449)
        {
            Content = new StringContent(
                """{"result":null,"operations":[{"type":"retry","step":0,"title":"Recalculate?","options":["Yes"]}]}""",
                Encoding.UTF8, "application/json"),
        });
        handler.Enqueue(Envelope(PoJson(typeId, "cars/1")));

        using (client)
        {
            // The three verbs added here are exactly the ones that can prompt, which is why they are
            // the only ones in this file that take an onRetry.
            await client.RefreshPersistentObjectAsync(
                new PersistentObject { Id = "cars/1", Name = "Car", ObjectTypeId = typeId, Attributes = [] },
                triggeredBy: "Brand",
                onRetry: (_, _) => Task.FromResult<RetryAnswer?>(RetryAnswer.Choose("Yes")));
        }

        handler.LastBody().GetProperty("retryResults").EnumerateArray().Should().ContainSingle();
    }

    // ------------------------------------------------------------------------------------------
    // Lookup references — bare JSON, route variables, no retry
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Listing_lookup_references_reads_a_bare_array()
    {
        var handler = new ScriptedHttpHandler();
        handler.Enqueue(Json("""[{"name":"CarBrand","isTransient":false,"valueCount":3,"displayType":0}]"""));

        using (var client = NewClient(handler))
        {
            // ⚠️ Not an envelope. Routing this through the envelope reader would return an empty
            // list for every call, with no exception to notice.
            var items = await client.ListLookupReferencesAsync();
            items.Should().ContainSingle();
            items[0].Name.Should().Be("CarBrand");
            items[0].ValueCount.Should().Be(3);
        }

        handler.Requests[^1].RequestUri!.AbsolutePath.Should().Be("/spark/lookupref/");
    }

    [Fact]
    public async Task An_unknown_lookup_reference_is_null_not_an_exception()
    {
        var handler = new ScriptedHttpHandler();
        handler.EnqueueStatus(HttpStatusCode.NotFound);

        using var client = NewClient(handler);
        (await client.GetLookupReferenceAsync("Missing")).Should().BeNull();
    }

    [Fact]
    public async Task A_lookup_reference_key_is_escaped_into_the_path()
    {
        var (client, handler) = NewClientWithWarmup();
        handler.EnqueueStatus(HttpStatusCode.NoContent);

        using (client)
        {
            // A key is user data. Unescaped, "a/b" would address a different route entirely.
            await client.DeleteLookupReferenceValueAsync("Car Brand", "a/b");
        }

        handler.Requests[^1].RequestUri!.AbsoluteUri
            .Should().EndWith("/spark/lookupref/Car%20Brand/a%2Fb");
        handler.Requests[^1].Method.Should().Be(HttpMethod.Delete);
    }

    [Fact]
    public async Task Adding_a_lookup_value_puts_the_value_in_the_body_unwrapped()
    {
        var (client, handler) = NewClientWithWarmup();
        handler.Enqueue(Json("""{"key":"Audi","values":{"en":"Audi"},"isActive":true}""", HttpStatusCode.Created));

        using (client)
        {
            var created = await client.AddLookupReferenceValueAsync(
                "CarBrand", new LookupReferenceValueDto { Key = "Audi", Values = TranslatedString.Create("Audi") });
            created.Key.Should().Be("Audi");
        }

        // Bare, not wrapped in a request envelope — this endpoint predates the literal route table
        // and still takes the DTO itself.
        handler.LastBody().GetProperty("key").GetString().Should().Be("Audi");
    }

    // ------------------------------------------------------------------------------------------
    // Metadata
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Listing_custom_actions_keeps_the_name_the_definition_does_not_carry()
    {
        var handler = new ScriptedHttpHandler();
        handler.Enqueue(Json(
            """[{"name":"MarkStolen","displayName":{"en":"Mark stolen"},"showedOn":"both","variant":"danger","offset":10}]"""));

        using (var client = NewClient(handler))
        {
            var actions = await client.ListCustomActionsAsync("Car");

            // `name` is the field a caller needs in order to invoke anything, and it is exactly the
            // field the server's own CustomActionDefinition does not have.
            actions.Single().Name.Should().Be("MarkStolen");
            actions.Single().Variant.Should().Be("danger");
        }


        handler.Requests[^1].RequestUri!.AbsolutePath.Should().Be("/spark/actions/list");
        handler.Requests[^1].Method.Should().Be(HttpMethod.Post);
        handler.LastBody().GetProperty("objectTypeId").GetString().Should().Be("Car");
    }

    [Fact]
    public async Task An_unknown_type_lists_no_actions_rather_than_404ing()
    {
        var handler = new ScriptedHttpHandler();
        handler.Enqueue(Json("[]"));

        using var client = NewClient(handler);

        // ⚠️ Empty is also the answer for a type the caller may not see. The endpoint refuses to
        // distinguish them, because the difference is an existence oracle — so a caller must not
        // read empty as "no such type".
        (await client.ListCustomActionsAsync(Guid.NewGuid())).Should().BeEmpty();
    }

    [Fact]
    public async Task Culture_translations_and_program_units_read_their_own_shapes()
    {
        var handler = new ScriptedHttpHandler();
        handler.Enqueue(Json("""{"languages":{"en":{"en":"English"},"nl":{"en":"Dutch"}},"defaultLanguage":"en"}"""));
        handler.Enqueue(Json("""{"common.save":{"en":"Save","nl":"Bewaren"}}"""));
        handler.Enqueue(Json("""{"programUnitGroups":[]}"""));

        using var client = NewClient(handler);

        (await client.GetCultureAsync()).DefaultLanguage.Should().Be("en");
        (await client.GetTranslationsAsync()).Should().ContainKey("common.save");
        (await client.GetProgramUnitsAsync()).Should().NotBeNull();

        handler.Requests.Select(r => r.RequestUri!.AbsolutePath).Should()
            .BeEquivalentTo(["/spark/culture", "/spark/translations", "/spark/program-units"]);
    }

    [Fact]
    public async Task An_entity_type_the_caller_may_not_see_is_null()
    {
        var handler = new ScriptedHttpHandler();
        handler.EnqueueStatus(HttpStatusCode.NotFound);

        using var client = NewClient(handler);
        (await client.GetEntityTypeAsync("Car")).Should().BeNull();
    }

    // ------------------------------------------------------------------------------------------
    // M6 — the viewer headers
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Neither_viewer_header_is_sent_by_default()
    {
        var handler = new ScriptedHttpHandler();
        handler.Enqueue(Json("""{"languages":{"en":{"en":"English"}},"defaultLanguage":"en"}"""));

        using var client = NewClient(handler);
        await client.GetCultureAsync();

        // ⚠️ The server falls back silently — UTC, and the default language — so this absence is
        // invisible from a response. It has to be asserted on the request.
        handler.Requests[0].Headers.Contains("X-Spark-Timezone").Should().BeFalse();
        handler.Requests[0].Headers.Contains("Accept-Language").Should().BeFalse();
    }

    [Fact]
    public async Task The_viewer_headers_are_sent_when_set()
    {
        var handler = new ScriptedHttpHandler();
        handler.Enqueue(Json("""{"languages":{"en":{"en":"English"},"nl":{"en":"Dutch"}},"defaultLanguage":"en"}"""));

        using var client = NewClient(handler);
        client.TimeZoneId = "Europe/Brussels";
        client.AcceptLanguage = "nl-BE,nl;q=0.9";

        await client.GetCultureAsync();

        handler.Requests[0].Headers.GetValues("X-Spark-Timezone").Single().Should().Be("Europe/Brussels");

        // Joined rather than Single(): Accept-Language is a known comma-separated header, so
        // HttpHeaders parses it into its two entries — and re-renders the q-value with a space
        // ("nl; q=0.9"). Both forms are the same header to any parser; asserting the string
        // verbatim would be asserting HttpClient's formatting, not this client's behaviour.
        string.Join(",", handler.Requests[0].Headers.GetValues("Accept-Language"))
            .Replace(" ", "").Should().Be("nl-BE,nl;q=0.9");
    }

    [Fact]
    public async Task The_viewer_headers_are_on_the_antiforgery_warmup_too()
    {
        var (client, handler) = NewClientWithWarmup();
        handler.EnqueueStatus(HttpStatusCode.NoContent);

        using (client)
        {
            client.TimeZoneId = "Asia/Kolkata";
            await client.DeleteLookupReferenceValueAsync("CarBrand", "Audi");
        }

        // The warmup builds its own request and goes straight to the inner HttpClient, so a header
        // attached only in SendAsync would miss it — and the asymmetry would surface much later, as
        // one response in an application coming back in the wrong language.
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/spark");
        handler.Requests[0].Headers.GetValues("X-Spark-Timezone").Single().Should().Be("Asia/Kolkata");
        handler.Requests[1].Headers.GetValues("X-Spark-Timezone").Single().Should().Be("Asia/Kolkata");
    }
}
