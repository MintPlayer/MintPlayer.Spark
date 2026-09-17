using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using Raven.Client.Documents.Linq;
using PO = MintPlayer.Spark.Abstractions.PersistentObject;
using POA = MintPlayer.Spark.Abstractions.PersistentObjectAttribute;

namespace MintPlayer.Spark.Tests.Endpoints.PersistentObject;

/// <summary>
/// A refresh triggered from inside an AsDetail attribute — both shapes of it.
///
/// <para>
/// None of this was covered server-side before #413: <c>NestedTrigger.TryParse</c>,
/// <c>BuildNestedRow</c> and the row lift were reachable only from the Angular client, and the only
/// tests that exercised them at all were Angular specs asserting the path the client <em>sends</em>.
/// Nothing asserted the server <em>parses</em> it.
/// </para>
/// <para>
/// The two shapes are <c>Rows[1].Kind</c> for an array grid and <c>Gate.Mode</c> for a single
/// embedded object. The pair of tests that matter most are the mismatch ones: a path whose shape
/// disagrees with the model must resolve to nothing rather than to the wrong row.
/// </para>
/// </summary>
public class NestedRefreshEndpointTests : SparkTestDriver
{
    private static readonly Guid PolicyTypeId = Guid.Parse("7e5f0000-0000-4000-8000-000000000101");
    private static readonly Guid GateTypeId = Guid.Parse("7e5f0000-0000-4000-8000-000000000102");
    private static readonly Guid RowTypeId = Guid.Parse("7e5f0000-0000-4000-8000-000000000103");

    private SparkEndpointFactory<NestedRefreshContext> _factory = null!;
    private SparkClient _client = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _factory = new SparkEndpointFactory<NestedRefreshContext>(Store, [
            NestedRefreshModels.Policy(PolicyTypeId),
            NestedRefreshModels.Gate(GateTypeId),
            NestedRefreshModels.Row(RowTypeId),
        ]);
        _client = new SparkClient(_factory.CreateClient(), ownsClient: true);
    }

    public override async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    /// <summary>
    /// The root object as the form posts it: AsDetail values travel as plain JSON under
    /// <c>value</c> — an object for the single gate, an array for the rows — because the client
    /// sends no <c>dataType</c> and the converter binds the base attribute.
    /// </summary>
    private static PO Policy(string gateMode, string? rowKind = "plain") => new()
    {
        Name = "NestedRefreshPolicy",
        ObjectTypeId = PolicyTypeId,
        Attributes =
        [
            new POA { Name = "Name", Value = "a policy" },
            new POA { Name = "Gate", Value = JsonDocument.Parse(
                $$"""{"Mode":"{{gateMode}}","Target":null,"Threshold":2}""").RootElement.Clone() },
            new POA { Name = "Rows", Value = JsonDocument.Parse(
                $$"""[{"Kind":"other","Note":"first"},{"Kind":"{{rowKind}}","Note":"second"}]""").RootElement.Clone() },
        ],
    };

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostRefreshAsync(PO obj, string? triggeredBy)
    {
        var response = await _client.SendAsync(
            HttpMethod.Post,
            "/spark/po/refresh",
            JsonContent.Create(Wire.Typed(PolicyTypeId, new { persistentObject = obj, triggeredBy })),
            requiresAntiforgery: true);

        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
    }

    private static JsonElement Result(JsonElement envelope) => envelope.GetProperty("result");

    private static JsonElement Attribute(JsonElement envelope, string name) =>
        Result(envelope).GetProperty("attributes")
            .EnumerateArray()
            .Single(a => a.GetProperty("name").GetString() == name);

    private static bool Has(JsonElement envelope, string name) =>
        Result(envelope).GetProperty("attributes")
            .EnumerateArray()
            .Any(a => a.GetProperty("name").GetString() == name);

    // ---- the single embedded object -------------------------------------------------------

    [Fact]
    public async Task A_single_embedded_object_is_addressed_without_an_index()
    {
        var (status, body) = await PostRefreshAsync(Policy("fixed"), "Gate.Mode");

        status.Should().Be(HttpStatusCode.OK);

        // The response describes the GATE, not the policy: the row's own attributes and nothing of
        // its parent's. This is what tells the client to apply it to the modal rather than the form.
        Has(body, "Target").Should().BeTrue();
        Has(body, "Name").Should().BeFalse();
    }

    [Fact]
    public async Task The_embedded_types_own_hook_reshapes_it()
    {
        var (_, fixedMode) = await PostRefreshAsync(Policy("fixed"), "Gate.Mode");
        var (_, autoMode) = await PostRefreshAsync(Policy("auto"), "Gate.Mode");

        // NestedRefreshGateActions, not NestedRefreshPolicyActions. The hook that owns a type's
        // shape is that type's own.
        Attribute(fixedMode, "Target").GetProperty("isVisible").GetBoolean().Should().BeTrue();
        Attribute(fixedMode, "Target").GetProperty("isRequired").GetBoolean().Should().BeTrue();
        Attribute(autoMode, "Target").GetProperty("isVisible").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task The_owning_object_reaches_the_hook_as_Parent()
    {
        // The gate hook copies its parent's Name onto an attribute of its own. Nothing else in the
        // request carries it, so the value proves obj.Parent was populated.
        var (_, body) = await PostRefreshAsync(Policy("fixed"), "Gate.Mode");

        Attribute(body, "ParentName").GetProperty("value").GetString().Should().Be("a policy");
    }

    // ---- the array grid, which must keep working -------------------------------------------

    [Fact]
    public async Task An_array_row_is_still_addressed_with_its_index()
    {
        var (status, body) = await PostRefreshAsync(Policy("auto", rowKind: "special"), "Rows[1].Kind");

        status.Should().Be(HttpStatusCode.OK);
        Has(body, "Note").Should().BeTrue();
        Has(body, "Name").Should().BeFalse();

        // Row 1 is the "special" one; row 0 is not. Addressing must pick the row it named.
        Attribute(body, "Note").GetProperty("value").GetString().Should().Be("second");
        Attribute(body, "Note").GetProperty("isReadOnly").GetBoolean().Should().BeTrue();
    }

    // ---- shape disagreements resolve to nothing --------------------------------------------

    [Fact]
    public async Task An_index_on_a_single_embedded_object_does_not_resolve()
    {
        // Gate is isArray: false. "Gate[0].Mode" is malformed rather than merely unusual, so it must
        // not reach the gate hook — it falls through to the root, whose hook does not know the
        // trigger and leaves the object alone.
        var (status, body) = await PostRefreshAsync(Policy("fixed"), "Gate[0].Mode");

        status.Should().Be(HttpStatusCode.OK);
        Has(body, "Name").Should().BeTrue();
        Has(body, "Target").Should().BeFalse();
    }

    [Fact]
    public async Task An_array_addressed_without_an_index_does_not_resolve()
    {
        // The inverse, and the sharper one: without the IsArray guard this would reach the
        // single-object reader and lift the whole array as if it were one row.
        var (status, body) = await PostRefreshAsync(Policy("auto"), "Rows.Kind");

        status.Should().Be(HttpStatusCode.OK);
        Has(body, "Name").Should().BeTrue();
        Has(body, "Note").Should().BeFalse();
    }

    [Fact]
    public async Task A_dotted_path_naming_something_that_is_not_an_AsDetail_reaches_the_root_hook()
    {
        // The grammar is permissive; the model decides. "Name.Whatever" parses as a nested trigger
        // and then fails to resolve, which is the fallthrough that makes widening the grammar safe.
        var (status, body) = await PostRefreshAsync(Policy("auto"), "Name.Whatever");

        status.Should().Be(HttpStatusCode.OK);
        Has(body, "Name").Should().BeTrue();
    }

    [Fact]
    public async Task A_bare_attribute_name_still_reaches_the_root_hook()
    {
        var (status, body) = await PostRefreshAsync(Policy("auto"), "Name");

        status.Should().Be(HttpStatusCode.OK);

        // The root hook stamps this, so its presence proves the root path ran rather than a nested
        // one silently swallowing the trigger.
        Attribute(body, "Summary").GetProperty("value").GetString().Should().Be("root hook ran");
    }

    // ---- redaction ---------------------------------------------------------------------------

    [Fact]
    public async Task A_refresh_inside_a_withheld_AsDetail_attribute_is_refused()
    {
        // GetProtectedAttributesAsync withholds Gate for a policy named "secret". Redaction is
        // coarse — it can withhold the whole attribute and nothing finer — so the correct answer
        // for a refresh addressed inside it is "no", not a row scaffolded from the model.
        //
        // Without this the nested branch returns before ApplyRedactionOf and hands back a populated
        // gate, including anything the gate's own hook wrote onto it.
        var id = await SeedPolicyAsync("secret");
        var obj = Policy("fixed");
        obj.Id = id;

        var (status, _) = await PostRefreshAsync(obj, "Gate.Mode");

        status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_refresh_inside_a_visible_AsDetail_attribute_is_allowed_for_a_stored_object()
    {
        // The other half of the pair: the same request against a policy whose Gate is not withheld
        // must still work, or the guard above would be indistinguishable from "nested refresh is
        // broken for anything that exists in the database".
        var id = await SeedPolicyAsync("ordinary");
        var obj = Policy("fixed");
        obj.Id = id;

        var (status, body) = await PostRefreshAsync(obj, "Gate.Mode");

        status.Should().Be(HttpStatusCode.OK);
        Attribute(body, "Target").GetProperty("isVisible").GetBoolean().Should().BeTrue();
    }

    private async Task<string> SeedPolicyAsync(string name)
    {
        using var session = Store.OpenAsyncSession();
        var policy = new NestedRefreshPolicy { Name = name };
        await session.StoreAsync(policy);
        await session.SaveChangesAsync();
        return policy.Id!;
    }
}

// ---- fixtures ------------------------------------------------------------------------------

public sealed class NestedRefreshPolicy
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Summary { get; set; }
    public NestedRefreshGate? Gate { get; set; }
    public List<NestedRefreshRow> Rows { get; set; } = [];
}

/// <summary>A single embedded object — the shape #413 exists for.</summary>
public sealed class NestedRefreshGate
{
    public string? Mode { get; set; }
    public double? Target { get; set; }
    public double Threshold { get; set; }
    public string? ParentName { get; set; }
}

/// <summary>A row of an array grid — the shape that already worked, and must keep working.</summary>
public sealed class NestedRefreshRow
{
    public string? Kind { get; set; }
    public string? Note { get; set; }
}

public class NestedRefreshContext : SparkContext
{
    public IRavenQueryable<NestedRefreshPolicy> NestedRefreshPolicies => Session.Query<NestedRefreshPolicy>();
}

public class NestedRefreshPolicyActions : DefaultPersistentObjectActions<NestedRefreshPolicy>
{
    public NestedRefreshPolicyActions(MintPlayer.Spark.Services.IEntityMapper mapper) : base(mapper) { }

    public override Task OnRefreshAsync(SparkRefreshArgs<NestedRefreshPolicy> args)
    {
        args.PersistentObject["Summary"].Value = "root hook ran";
        return Task.CompletedTask;
    }

    public override Task<IReadOnlyCollection<string>?> GetProtectedAttributesAsync(
        string action, NestedRefreshPolicy entity)
        => Task.FromResult<IReadOnlyCollection<string>?>(
            entity.Name == "secret" ? [nameof(NestedRefreshPolicy.Gate)] : null);
}

public class NestedRefreshGateActions : DefaultPersistentObjectActions<NestedRefreshGate>
{
    public NestedRefreshGateActions(MintPlayer.Spark.Services.IEntityMapper mapper) : base(mapper) { }

    public override Task OnRefreshAsync(SparkRefreshArgs<NestedRefreshGate> args)
    {
        var obj = args.PersistentObject;
        var isFixed = obj["Mode"].Value?.ToString() == "fixed";

        obj["Target"].IsVisible = isFixed;
        obj["Target"].IsRequired = isFixed;

        // Read-only context, proving the row is handed its owner.
        obj["ParentName"].Value = obj.Parent?["Name"].Value;

        return Task.CompletedTask;
    }
}

public class NestedRefreshRowActions : DefaultPersistentObjectActions<NestedRefreshRow>
{
    public NestedRefreshRowActions(MintPlayer.Spark.Services.IEntityMapper mapper) : base(mapper) { }

    public override Task OnRefreshAsync(SparkRefreshArgs<NestedRefreshRow> args)
    {
        var obj = args.PersistentObject;
        obj["Note"].IsReadOnly = obj["Kind"].Value?.ToString() == "special";
        return Task.CompletedTask;
    }
}

public static class NestedRefreshModels
{
    public static EntityTypeFile Policy(Guid id) => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = id,
            Name = "NestedRefreshPolicy",
            ClrType = typeof(NestedRefreshPolicy).FullName!,
            Attributes =
            [
                new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(), Name = "Name", DataType = "string", Order = 1,
                    TriggersRefresh = true,
                },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Summary", DataType = "string", Order = 2 },
                new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(), Name = "Gate", DataType = "AsDetail", Order = 3,
                    AsDetailType = typeof(NestedRefreshGate).FullName!, IsArray = false,
                },
                new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(), Name = "Rows", DataType = "AsDetail", Order = 4,
                    AsDetailType = typeof(NestedRefreshRow).FullName!, IsArray = true,
                },
            ],
        }
    };

    public static EntityTypeFile Gate(Guid id) => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = id,
            Name = "NestedRefreshGate",
            ClrType = typeof(NestedRefreshGate).FullName!,
            Attributes =
            [
                new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(), Name = "Mode", DataType = "string", Order = 1,
                    TriggersRefresh = true,
                },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Target", DataType = "decimal", Order = 2 },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Threshold", DataType = "decimal", Order = 3 },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "ParentName", DataType = "string", Order = 4 },
            ],
        }
    };

    public static EntityTypeFile Row(Guid id) => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = id,
            Name = "NestedRefreshRow",
            ClrType = typeof(NestedRefreshRow).FullName!,
            Attributes =
            [
                new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(), Name = "Kind", DataType = "string", Order = 1,
                    TriggersRefresh = true,
                },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Note", DataType = "string", Order = 2 },
            ],
        }
    };
}
