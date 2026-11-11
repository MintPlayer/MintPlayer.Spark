using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Model;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Endpoints.PersistentObject;

/// <summary>
/// A Reference picker's <b>Add new</b> (reference add-new PRD) against the real route table, rights
/// and RavenDB: <c>/po/new</c> with the picker context (D8, D12, D17), the name-resolved hook of a
/// type with no C# class (R10), and the create that confirms instead of storing (D10, D18).
/// </summary>
/// <remarks>
/// Every fixture type is prefixed <c>Ran</c>: actions classes are found by short name across the
/// test assembly, so a second <c>SupplierActions</c> anywhere would be picked up here.
/// </remarks>
public class ReferenceAddNewEndpointTests : SparkTestDriver
{
    private static readonly Guid ShopTypeId = Guid.Parse("7a2e0000-0000-4000-8000-7a2e00000001");
    private static readonly Guid SupplierTypeId = Guid.Parse("7a2e0000-0000-4000-8000-7a2e00000002");
    private static readonly Guid LabelTypeId = Guid.Parse("7a2e0000-0000-4000-8000-7a2e00000003");
    private static readonly Guid TagTypeId = Guid.Parse("7a2e0000-0000-4000-8000-7a2e00000004");

    public class RanShop
    {
        public string? Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Supplier { get; set; }
        public string? PlainSupplier { get; set; }
        public List<string> Labels { get; set; } = [];
        public string? Tag { get; set; }
    }

    public class RanSupplier
    {
        public string? Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Origin { get; set; }
    }

    /// <summary>A value object: no documents of its own, keyed by <see cref="Key"/>.</summary>
    public class RanLabel
    {
        public string Key { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
    }

    private class RanContext : SparkContext
    {
        public IQueryable<RanShop> RanShops => Session.Query<RanShop>();
        public IQueryable<RanSupplier> RanSuppliers => Session.Query<RanSupplier>();
    }

    /// <summary>A value object whose key derives from its name (IHasNaturalId; owner 2026-11-07, revising D18).</summary>
    public class RanCode : IHasNaturalId
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public static string GetId(string? name) => name?.Trim() ?? string.Empty;
        string IHasNaturalId.GetId() => GetId(Name);
    }

    static ReferenceAddNewEndpointTests()
    {
        SparkValueObjects.Register(typeof(RanLabel), nameof(RanLabel.Key), row => ((RanLabel)row).Key);
        SparkValueObjects.Register(typeof(RanCode), nameof(RanCode.Id), row => ((RanCode)row).Id);
    }

    /// <summary>What the last <c>OnNewAsync</c> saw. One per host, so one per test.</summary>
    public sealed class Recorder
    {
        public string? ReferenceAttribute;
        public string? ParentType;
        public string? ParentId;
        public string? ParentName;
        public string? GrandParentName;
        public string? SearchTerm;
        public string? NameAtHook;
        public int RequestsAtHook;
    }

    public class RanShopActions : DefaultPersistentObjectActions<RanShop>, ISparkOwnsRowSecurity
    {
        public RanShopActions(IEntityMapper mapper) : base(mapper) { }
        public string RowSecurityRationale => "Test fixture: every shop is written by the test that reads it.";
    }

    public class RanSupplierActions : DefaultPersistentObjectActions<RanSupplier>, ISparkOwnsRowSecurity
    {
        private readonly Recorder recorder;
        private readonly IAsyncDocumentSession session;

        public RanSupplierActions(IEntityMapper mapper, Recorder recorder, IAsyncDocumentSession session) : base(mapper)
        {
            this.recorder = recorder;
            this.session = session;
        }

        public string RowSecurityRationale => "Test fixture: every supplier is written by the test that reads it.";

        public override async Task OnNewAsync(SparkNewArgs args)
        {
            await base.OnNewAsync(args);
            recorder.RequestsAtHook = session.Advanced.NumberOfRequests;
            recorder.ReferenceAttribute = args.ReferenceAttribute?.Name;
            recorder.ParentType = args.ParentType?.Name;
            recorder.ParentId = args.Parent?.Id;
            recorder.ParentName = args.Parent?.Attributes.FirstOrDefault(a => a.Name == "Name")?.Value?.ToString();
            recorder.GrandParentName = args.Parent?.Parent?.Attributes.FirstOrDefault(a => a.Name == "Name")?.Value?.ToString();
            recorder.SearchTerm = args.Parameters.TryGetValue("searchTerm", out var term) ? term : null;
            recorder.NameAtHook = args.PersistentObject["Name"].Value?.ToString();
            if (args.ReferenceAttribute is not null)
                args.PersistentObject["Origin"].SetOriginalValue("picker");
        }
    }

    /// <summary>The actions of a type with no C# class, found by name (R10). No base class to derive from.</summary>
    public class RanTagActions
    {
        public Task OnNewAsync(SparkNewArgs args)
        {
            args.PersistentObject["Origin"].SetOriginalValue(args.ReferenceAttribute?.Name);
            return Task.CompletedTask;
        }
    }

    private static EntityAttributeDefinition Attr(string name, string dataType = "string", bool readOnly = false, bool required = false)
        => new() { Id = Guid.NewGuid(), Name = name, DataType = dataType, IsReadOnly = readOnly, IsRequired = required };

    private static EntityAttributeDefinition Reference(string name, string query, string? referenceType, bool canAddNew, string? defaultAttribute = null, bool isArray = false)
        => new()
        {
            Id = Guid.NewGuid(), Name = name, DataType = "Reference", Query = query, ReferenceType = referenceType,
            CanAddNewReference = canAddNew ? true : null, DefaultAttribute = defaultAttribute, IsArray = isArray,
        };

    private static EntityTypeFile ShopModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = ShopTypeId,
            Name = "RanShop",
            ClrType = typeof(RanShop).FullName!,
            Breadcrumb = "{Name}",
            Attributes =
            [
                Attr("Name"),
                Reference("Supplier", "RanGetSuppliers", typeof(RanSupplier).FullName, canAddNew: true, defaultAttribute: "Name"),
                Reference("PlainSupplier", "RanGetSuppliers", typeof(RanSupplier).FullName, canAddNew: false),
                Reference("Labels", "RanGetLabels", typeof(RanLabel).FullName, canAddNew: true, defaultAttribute: "Text", isArray: true),
                Reference("Tag", "RanGetTags", referenceType: null, canAddNew: true, defaultAttribute: "Text"),
            ],
        },
        Queries = [new SparkQuery { Id = Guid.NewGuid(), Name = "RanGetShops", Source = "Database.RanShops", EntityType = "RanShop" }],
    };

    private static EntityTypeFile SupplierModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = SupplierTypeId,
            Name = "RanSupplier",
            ClrType = typeof(RanSupplier).FullName!,
            Breadcrumb = "{Name}",
            Attributes = [Attr("Name", required: true), Attr("Origin")],
        },
        Queries = [new SparkQuery { Id = Guid.NewGuid(), Name = "RanGetSuppliers", Source = "Database.RanSuppliers", EntityType = "RanSupplier" }],
    };

    private static EntityTypeFile LabelModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = LabelTypeId,
            Name = "RanLabel",
            ClrType = typeof(RanLabel).FullName!,
            Breadcrumb = "{Text}",
            Attributes = [Attr("Key"), Attr("Text")],
        },
        Queries = [new SparkQuery { Id = Guid.NewGuid(), Name = "RanGetLabels", Source = "Custom.RanLabels", EntityType = "RanLabel" }],
    };

    private static readonly Guid CodeTypeId = Guid.Parse("7a2e0000-0000-4000-8000-7a2e00000005");

    private static EntityTypeFile CodeModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = CodeTypeId,
            Name = "RanCode",
            ClrType = typeof(RanCode).FullName!,
            Breadcrumb = "{Name}",
            Attributes = [Attr("Name")],
        },
        Queries = [new SparkQuery { Id = Guid.NewGuid(), Name = "RanGetCodes", Source = "Custom.RanCodes", EntityType = "RanCode" }],
    };

    /// <summary>No <c>ClrType</c>: a JSON-only virtual type.</summary>
    private static EntityTypeFile TagModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = TagTypeId,
            Name = "RanTag",
            Breadcrumb = "{Text}",
            Attributes = [Attr("Text"), Attr("Origin")],
        },
        Queries = [new SparkQuery { Id = Guid.NewGuid(), Name = "RanGetTags", Source = "Custom.RanTags", EntityType = "RanTag" }],
    };

    private SparkEndpointFactory<RanContext> _factory = null!;
    private HttpClient _client = null!;
    private string _cookieHeader = null!;
    private string _xsrfToken = null!;
    private readonly Recorder _recorder = new();

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _factory = new SparkEndpointFactory<RanContext>(
            Store,
            [ShopModel(), SupplierModel(), LabelModel(), TagModel(), CodeModel()],
            configureServices: services =>
            {
                services.AddSingleton(_recorder);
                services.AddScoped<RanShopActions>();
                services.AddScoped<RanSupplierActions>();
                services.AddScoped<RanTagActions>();
            },
            security: SparkTestSecurity.Permissive);
        _client = _factory.CreateClient();
        (_cookieHeader, _xsrfToken) = await _factory.MintAntiforgeryAsync();
    }

    public override async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
            await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(string url, object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload) };
        request.Headers.Add("Cookie", _cookieHeader);
        request.Headers.Add("X-XSRF-TOKEN", _xsrfToken);

        var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static JsonElement Attribute(JsonElement body, string name) =>
        body.GetProperty("result").GetProperty("attributes").EnumerateArray()
            .Single(a => a.GetProperty("name").GetString() == name);

    /// <summary>A shop owner level as the client posts it: values, with an id only when stored.</summary>
    private static object Owner(string? id, string name, object? parent = null) => new
    {
        id,
        name = "RanShop",
        objectTypeId = ShopTypeId,
        attributes = new[] { new { name = "Name", value = name, isValueChanged = true } },
        parent,
    };

    private static object PickerNew(string attribute, object? parent, string? searchTerm = null) => new
    {
        parentType = ShopTypeId.ToString(),
        referenceAttribute = attribute,
        parent,
        parameters = searchTerm is null ? null : new Dictionary<string, string> { ["searchTerm"] = searchTerm },
    };

    private async Task<RanShop> SeedShopAsync(string name)
    {
        var shop = new RanShop { Name = name };
        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(shop);
        await session.SaveChangesAsync();
        return shop;
    }

    // ---- /po/new from a picker -----------------------------------------------------------------

    [Fact]
    public async Task New_from_a_picker_prefills_the_default_attribute_and_hands_the_hook_its_context()
    {
        var (status, body) = await PostAsync("/spark/po/new",
            Wire.Typed(SupplierTypeId, PickerNew("Supplier", Owner(null, "Draft shop"), "  Acme  ")));

        status.Should().Be(HttpStatusCode.OK);
        Attribute(body, "Name").GetProperty("value").GetString().Should().Be("Acme", "the trimmed term fills defaultAttribute (D17)");
        Attribute(body, "Origin").GetProperty("value").GetString().Should().Be("picker");
        _recorder.NameAtHook.Should().Be("Acme", "the prefill runs before OnNewAsync, so the hook can change or lock it");
        _recorder.ReferenceAttribute.Should().Be("Supplier");
        _recorder.ParentType.Should().Be("RanShop");
        _recorder.ParentId.Should().BeNullOrEmpty("the owner is unsaved");
        _recorder.ParentName.Should().Be("Draft shop", "an unsaved owner level carries its posted values (D12)");
        _recorder.SearchTerm.Should().Be("  Acme  ", "Parameters stay raw");
    }

    [Fact]
    public async Task New_from_a_picker_rereads_a_stored_owner_instead_of_trusting_the_posted_values()
    {
        var shop = await SeedShopAsync("Stored shop");

        var (status, _) = await PostAsync("/spark/po/new",
            Wire.Typed(SupplierTypeId, PickerNew("Supplier", Owner(shop.Id, "Forged name"))));

        status.Should().Be(HttpStatusCode.OK);
        _recorder.ParentId.Should().Be(shop.Id);
        _recorder.ParentName.Should().Be("Stored shop", "a stored level is authoritative, never the caller's copy");
    }

    [Fact]
    public async Task New_from_a_picker_loads_the_stored_owner_chain_in_one_round_trip()
    {
        var first = await SeedShopAsync("First");
        var second = await SeedShopAsync("Second");
        var third = await SeedShopAsync("Third");

        await PostAsync("/spark/po/new", Wire.Typed(SupplierTypeId, PickerNew("Supplier", Owner(first.Id, "x"))));
        var oneLevel = _recorder.RequestsAtHook;

        var (status, _) = await PostAsync("/spark/po/new", Wire.Typed(SupplierTypeId,
            PickerNew("Supplier", Owner(first.Id, "x", Owner(second.Id, "y", Owner(third.Id, "z"))))));

        status.Should().Be(HttpStatusCode.OK);
        _recorder.GrandParentName.Should().Be("Second");
        _recorder.RequestsAtHook.Should().Be(oneLevel, "every stored level is one lazy load in a single batch (D12), not N+1 reads");
    }

    [Fact]
    public async Task New_from_a_picker_is_refused_for_a_reference_without_canAddNewReference()
    {
        var (status, _) = await PostAsync("/spark/po/new",
            Wire.Typed(SupplierTypeId, PickerNew("PlainSupplier", Owner(null, "Draft"))));

        status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task New_from_a_picker_is_refused_when_the_reference_targets_another_type()
    {
        var (status, _) = await PostAsync("/spark/po/new",
            Wire.Typed(ShopTypeId, PickerNew("Supplier", Owner(null, "Draft"))));

        status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task New_from_a_picker_is_refused_with_a_parent_id()
    {
        var shop = await SeedShopAsync("Stored");

        var (status, _) = await PostAsync("/spark/po/new", Wire.Typed(SupplierTypeId, new
        {
            parentType = ShopTypeId.ToString(),
            parentId = shop.Id,
            referenceAttribute = "Supplier",
        }));

        status.Should().Be(HttpStatusCode.NotFound, "the owner travels as Parent, never as ParentId");
    }

    [Fact]
    public async Task New_from_a_picker_is_refused_when_the_posted_owner_is_another_type()
    {
        var (status, _) = await PostAsync("/spark/po/new", Wire.Typed(SupplierTypeId, PickerNew("Supplier", new
        {
            name = "RanSupplier",
            objectTypeId = SupplierTypeId,
            attributes = Array.Empty<object>(),
        })));

        status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task New_from_a_picker_runs_the_name_resolved_hook_of_a_type_with_no_class()
    {
        var (status, body) = await PostAsync("/spark/po/new",
            Wire.Typed(TagTypeId, PickerNew("Tag", Owner(null, "Draft"), "urgent")));

        status.Should().Be(HttpStatusCode.OK);
        Attribute(body, "Origin").GetProperty("value").GetString().Should().Be("Tag", "RanTagActions.OnNewAsync ran (R10)");
        Attribute(body, "Text").GetProperty("value").GetString().Should().Be("urgent");
    }

    [Fact]
    public async Task A_scaffolded_owner_carries_the_effective_flag()
    {
        var (status, body) = await PostAsync("/spark/po/new", Wire.Typed(ShopTypeId));

        status.Should().Be(HttpStatusCode.OK);
        Attribute(body, "Supplier").GetProperty("canAddNewReference").GetBoolean().Should().BeTrue();
        Attribute(body, "PlainSupplier").TryGetProperty("canAddNewReference", out _).Should().BeFalse("written only when true (D7)");
    }

    // ---- /po/create of a type without documents ------------------------------------------------

    private static object CreateBody(Guid typeId, string typeName, params (string Name, string? Value)[] values) => new
    {
        persistentObject = new
        {
            name = typeName,
            objectTypeId = typeId,
            attributes = values.Select(v => new { name = v.Name, value = v.Value, isValueChanged = true }).ToArray(),
        },
    };

    [Fact]
    public async Task Create_of_a_value_object_confirms_it_with_its_key_as_id_and_stores_nothing()
    {
        var (status, body) = await PostAsync("/spark/po/create",
            Wire.Typed(LabelTypeId, CreateBody(LabelTypeId, "RanLabel", ("Key", "blue"), ("Text", "Blue"))));

        status.Should().Be(HttpStatusCode.OK, "confirmed, not created (D10)");
        body.GetProperty("result").GetProperty("id").GetString().Should().Be("blue", "a value object's id is its [ValueKey] (D18)");

        using var session = Store.OpenAsyncSession();
        (await session.LoadAsync<object>("blue")).Should().BeNull("a value object has no document of its own (D11)");
    }

    [Fact]
    public async Task Create_of_a_value_object_without_a_key_is_a_validation_error()
    {
        var (status, _) = await PostAsync("/spark/po/create",
            Wire.Typed(LabelTypeId, CreateBody(LabelTypeId, "RanLabel", ("Text", "Blue"))));

        status.Should().Be(HttpStatusCode.BadRequest, "an empty key is never replaced by a made-up id (D18)");
    }

    /// <summary>A natural id is the key: the id is derived from the posted name (owner 2026-11-07, revising D18).</summary>
    [Fact]
    public async Task Create_of_a_natural_id_value_object_confirms_it_with_its_derived_id()
    {
        var (status, body) = await PostAsync("/spark/po/create",
            Wire.Typed(CodeTypeId, CreateBody(CodeTypeId, "RanCode", ("Name", " build "))));

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("result").GetProperty("id").GetString().Should().Be("build");
    }

    [Fact]
    public async Task Create_of_a_natural_id_value_object_with_an_empty_name_is_a_validation_error()
    {
        var (status, _) = await PostAsync("/spark/po/create",
            Wire.Typed(CodeTypeId, CreateBody(CodeTypeId, "RanCode", ("Name", "  "))));

        status.Should().Be(HttpStatusCode.BadRequest, "an empty derived key is never replaced by a made-up id (D18)");
    }

    [Fact]
    public async Task Create_of_a_type_with_no_class_confirms_it_with_a_new_id()
    {
        var (status, body) = await PostAsync("/spark/po/create",
            Wire.Typed(TagTypeId, CreateBody(TagTypeId, "RanTag", ("Text", "urgent"))));

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("result").GetProperty("id").GetString().Should().NotBeNullOrWhiteSpace();
        Attribute(body, "Text").GetProperty("value").GetString().Should().Be("urgent");
    }
}
