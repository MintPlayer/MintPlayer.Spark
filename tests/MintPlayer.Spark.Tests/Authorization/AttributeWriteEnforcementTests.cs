using MintPlayer.Spark.Tests._Infrastructure;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.Model;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Models;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Authorization;

using Po = Abstractions.PersistentObject;

// Fixture names start with "Wr": ActionsResolver matches actions classes by simple name across the
// whole assembly.
public class WrTag
{
    public string? Id { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>An AsDetail row with its own attributes, keyed (registered in the test's static ctor).</summary>
public class WrLine
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = "";
    public int Qty { get; set; }
}

/// <summary>A single embedded (AsDetail, not array) object.</summary>
public class WrSpec
{
    public string Color { get; set; } = "";
    public int Size { get; set; }
}

/// <summary>One property of every attribute kind the mapper writes.</summary>
public class WrItem
{
    public string? Id { get; set; }
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public string? Pin { get; set; }
    public bool Locked { get; set; }
    public TranslatedString? Title { get; set; }
    public string? Tag { get; set; }
    public string[] Tags { get; set; } = [];
    public List<WrLine> Lines { get; set; } = [];
    public WrSpec? Spec { get; set; }
}

/// <summary>Residual (c): a nested entity class, whose CLR name ends <c>WrOuter+WrNested</c>.</summary>
public class WrOuter
{
    public class WrNested
    {
        public string? Id { get; set; }
        public string Name { get; set; } = "";
    }
}

/// <summary>What the per-row hook protects on a locked item; set per test.</summary>
public sealed class WrProtection
{
    public List<string> Names { get; } = [];
}

public class WrItemActions(IEntityMapper mapper, WrProtection protection)
    : DefaultPersistentObjectActions<WrItem>(mapper), Abstractions.Authorization.ISparkOwnsRowSecurity
{
    public string RowSecurityRationale => "Test fixture: every row is visible; the subject is attribute rights.";

    public override Task<IReadOnlyCollection<string>?> GetProtectedAttributesAsync(string action, WrItem entity)
        => Task.FromResult<IReadOnlyCollection<string>?>(entity.Locked && protection.Names.Count > 0 ? [.. protection.Names] : null);
}

public class WrTagActions(IEntityMapper mapper) : DefaultPersistentObjectActions<WrTag>(mapper), Abstractions.Authorization.ISparkOwnsRowSecurity
{
    public string RowSecurityRationale => "Test fixture.";
}

public class WrLineActions(IEntityMapper mapper) : DefaultPersistentObjectActions<WrLine>(mapper), Abstractions.Authorization.ISparkOwnsRowSecurity
{
    public string RowSecurityRationale => "Test fixture.";
}

public class WrSpecActions(IEntityMapper mapper) : DefaultPersistentObjectActions<WrSpec>(mapper), Abstractions.Authorization.ISparkOwnsRowSecurity
{
    public string RowSecurityRationale => "Test fixture.";
}

public class WrNestedActions(IEntityMapper mapper) : DefaultPersistentObjectActions<WrOuter.WrNested>(mapper), Abstractions.Authorization.ISparkOwnsRowSecurity
{
    public string RowSecurityRationale => "Test fixture.";
}

/// <summary>Residual (a): prompts with the item, its Pin filled by the action itself.</summary>
public sealed class WrPromptAction(IDatabaseAccess databaseAccess, IRetryAccessor retry) : ICustomAction
{
    public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        var po = await databaseAccess.GetPersistentObjectAsync(AttributeWriteEnforcementTests.ItemTypeId, "items/1");
        po!["Pin"].Value = "p0-stored";
        retry.Action("Confirm", ["OK", "Cancel"], persistentObject: po);
    }
}

public class WrContext : SparkContext
{
    public IRavenQueryable<WrItem> Items => Session.Query<WrItem>();
    public IRavenQueryable<WrTag> Tags => Session.Query<WrTag>();
}

/// <summary>
/// Write-side enforcement of attribute-level rights (contributions M2c-2b, PRD §5 Q14, leaks 2, 6
/// and 7) and the per-row blanking, end to end over HTTP: W1 the Update echo, W2 the shield on every
/// attribute kind, W3 the create path, W5 indistinguishable blanking, and the M2c-2a residuals
/// (a) retry prompts, (b) dotted per-row names in an embedded row's breadcrumb, (c) nested classes.
/// </summary>
public class AttributeWriteEnforcementTests : SparkTestDriver
{
    internal static readonly Guid ItemTypeId = Guid.Parse("a77b0000-0000-4000-8000-00000000b001");
    private static readonly Guid TagTypeId = Guid.Parse("a77b0000-0000-4000-8000-00000000b002");
    private static readonly Guid LineTypeId = Guid.Parse("a77b0000-0000-4000-8000-00000000b003");
    private static readonly Guid SpecTypeId = Guid.Parse("a77b0000-0000-4000-8000-00000000b004");
    private static readonly Guid NestedTypeId = Guid.Parse("a77b0000-0000-4000-8000-00000000b005");

    private const string Prompt = "WrPrompt";

    /// <summary>Type rights on every fixture type; attribute rights layered per test.</summary>
    private static readonly string[] TypeGrants =
        ["QueryReadEditNewDelete/WrItem", "QueryReadEditNew/WrTag", "QueryReadEditNewDelete/WrLine", "QueryReadEditNew/WrSpec", $"{Prompt}/WrItem"];

    static AttributeWriteEnforcementTests()
        => SparkValueObjects.Register(typeof(WrLine), "Id", row => ((WrLine)row).Id);

    private readonly List<IAsyncDisposable> factories = [];
    private readonly List<IDisposable> clients = [];

    public override async Task DisposeAsync()
    {
        foreach (var client in clients)
            client.Dispose();
        foreach (var factory in factories)
            await factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private async Task<(SparkClient Client, WrProtection Protection)> StartAsync(SparkTestSecurity security, params string[] protectedNames)
    {
        var protection = new WrProtection();
        protection.Names.AddRange(protectedNames);

        var factory = new SparkEndpointFactory<WrContext>(
            Store,
            [ItemModel(), TagModel(), LineModel(), SpecModel(), NestedModel()],
            configureServices: services =>
            {
                services.AddSingleton(protection);
                services.AddScoped<WrItemActions>();
                services.AddScoped<WrTagActions>();
                services.AddScoped<WrLineActions>();
                services.AddScoped<WrSpecActions>();
                services.AddScoped<WrNestedActions>();
                services.AddScoped<WrPromptAction>();
                services.AddSingleton(TestActions.LoaderWithCustom(Prompt));
                services.AddScoped<ICustomActionResolver>(sp => new StubActionResolver(Prompt, sp.GetRequiredService<WrPromptAction>()));
            },
            security: security);
        factories.Add(factory);
        var client = new SparkClient(factory.CreateClient(), ownsClient: true);
        clients.Add(client);

        await SeedAsync(async session =>
        {
            await session.StoreAsync(new WrTag { Name = "one" }, "tags/1");
            await session.StoreAsync(new WrTag { Name = "two" }, "tags/2");
            await session.StoreAsync(Seed(locked: true), "items/1");
            await session.StoreAsync(new WrItem { Name = "empty" }, "items/2");
        });

        return (client, protection);
    }

    private static WrItem Seed(bool locked) => new()
    {
        Name = "n0",
        Code = "kode-stored",
        Pin = "p0-stored",
        Locked = locked,
        Title = TranslatedString.Create("t0"),
        Tag = "tags/1",
        Tags = ["tags/1"],
        Lines = [new WrLine { Id = "l1", Label = "a", Qty = 1 }],
        Spec = new WrSpec { Color = "red", Size = 1 },
    };

    private static SparkTestSecurity Rights(string[]? denying = null, params string[] granting)
        => SparkTestSecurity.Empty.Granting([.. TypeGrants, .. granting]).Denying(denying ?? []);

    // ---- W1: the Update echo -----------------------------------------------------------------------

    [Fact]
    public async Task The_update_response_never_echoes_a_protected_or_Read_denied_stored_value()
    {
        var (client, _) = await StartAsync(Rights(denying: ["QueryRead/WrItem/Code"]), "Pin");

        var po = await client.GetPersistentObjectAsync(ItemTypeId, "items/1");
        po!.Attributes.Select(a => a.Name).Should().NotContain("Code");
        po["Pin"].Value.Should().BeNull("the load blanks the protected Pin");
        po["Name"].Value = "renamed";
        po["Name"].IsValueChanged = true;

        var saved = await client.UpdatePersistentObjectAsync(po);

        var json = JsonSerializer.Serialize(saved);
        json.Should().NotContain("p0-stored", "the per-row protected stored value must not be echoed");
        json.Should().NotContain("kode-stored", "the Read-denied stored value must not be echoed");
        saved.Attributes.Select(a => a.Name).Should().NotContain("Code");
        saved["Pin"].Value.Should().BeNull();
        saved["Name"].Value?.ToString().Should().Be("renamed");
        saved.Etag.Should().NotBeNullOrEmpty().And.NotBe(po.Etag, "the client needs the fresh etag for its next save");
        (await LoadAsync("items/1")).Pin.Should().Be("p0-stored", "the blank posted back did not wipe the stored value");
    }

    // ---- W2: the shield covers every attribute kind ------------------------------------------------

    /// <summary>
    /// Every attribute of <see cref="WrItem"/> except <c>Name</c> is refused — by a static
    /// <c>Edit/WrItem/{Attr}</c> denial generated from the CLR type, or by the per-row hook — and the
    /// client posts a changed value for every one of them, reflection-driven, flagged changed or not.
    /// Only <c>Name</c> may change in the stored document.
    /// </summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task Only_the_writable_attribute_changes_whatever_kind_the_others_are(bool perRow, bool flagged)
    {
        var others = WritableProperties(typeof(WrItem)).Select(p => p.Name).Where(n => n != "Name").ToArray();
        new[] { "Code", "Title", "Tag", "Tags", "Lines", "Spec" }.Except(others).Should().BeEmpty("the fixture must hold every attribute kind");

        var (client, _) = perRow
            ? await StartAsync(Rights(), others)
            : await StartAsync(Rights(denying: [.. others.Select(n => $"Edit/WrItem/{n}")]));
        var before = await LoadAsync("items/1");

        var po = await client.GetPersistentObjectAsync(ItemTypeId, "items/1");
        foreach (var attribute in po!.Attributes)
            Change(attribute, typeof(WrItem), flagged);

        await client.UpdatePersistentObjectAsync(po);

        var after = await LoadAsync("items/1");
        after.Name.Should().Be("changed-Name", "Name is writable");
        foreach (var property in WritableProperties(typeof(WrItem)).Where(p => p.Name != "Name"))
        {
            Json(property.GetValue(after)).Should().Be(Json(property.GetValue(before)),
                $"{property.Name} ({property.PropertyType.Name}) may not be written");
        }
    }

    /// <summary>The shield inside AsDetail rows: by the row type's static rights, or a dotted per-row name.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_row_attribute_the_caller_may_not_edit_keeps_its_stored_value(bool perRow)
    {
        var (client, _) = perRow
            ? await StartAsync(Rights(), "Lines.Label")
            : await StartAsync(Rights(denying: ["Edit/WrLine/Label"]));

        var po = await client.GetPersistentObjectAsync(ItemTypeId, "items/1");
        var lines = (PersistentObjectAttributeAsDetail)po!["Lines"];
        var row = lines.Objects!.Single();
        row["Label"].Value = "changed";
        row["Label"].IsValueChanged = true;
        row["Qty"].Value = 99;
        row["Qty"].IsValueChanged = true;
        lines.Objects = [row, NewRow("added", 5)];
        lines.IsValueChanged = true;

        await client.UpdatePersistentObjectAsync(po);

        var after = await LoadAsync("items/1");
        after.Lines.Should().HaveCount(2);
        after.Lines[0].Label.Should().Be("a", "Label is not editable on a stored row");
        after.Lines[0].Qty.Should().Be(99, "Qty is");
        after.Lines[1].Qty.Should().Be(5);
        after.Lines[1].Label.Should().Be(perRow ? "" : "added",
            perRow ? "the owner's hook protects the column on every row of this document" : "New on the row type is not denied");
    }

    // ---- W3: the create path ----------------------------------------------------------------------

    [Fact]
    public async Task A_create_drops_values_for_New_denied_attributes_and_is_not_refused()
    {
        var (client, _) = await StartAsync(Rights(denying: ["New/WrItem/Code", "New/WrItem/Tag", "New/WrItem/Lines", "New/WrItem/Title"]));

        var po = await client.NewPersistentObjectAsync(ItemTypeId);
        Set(po, "Name", "created");
        Set(po, "Code", "c-new");
        Set(po, "Tag", "tags/1");
        Set(po, "Title", new Dictionary<string, string> { ["en"] = "t-new" });
        var lines = (PersistentObjectAttributeAsDetail)po["Lines"];
        lines.Objects = [NewRow("r", 1)];
        lines.IsValueChanged = true;

        var created = await client.CreatePersistentObjectAsync(po);

        var stored = await LoadAsync(created.Id!);
        stored.Name.Should().Be("created");
        stored.Code.Should().Be("", "the field initializer stays");
        stored.Tag.Should().BeNull();
        stored.Title.Should().BeNull();
        stored.Lines.Should().BeEmpty();
    }

    // ---- W5: per-row blanking is indistinguishable from empty --------------------------------------

    [Fact]
    public async Task A_blanked_attribute_is_byte_identical_to_a_genuinely_empty_one()
    {
        var (client, _) = await StartAsync(Rights(), "Pin", "Tag", "Lines", "Spec");

        var blanked = await client.GetPersistentObjectAsync(ItemTypeId, "items/1");
        var empty = await client.GetPersistentObjectAsync(ItemTypeId, "items/2");

        foreach (var name in new[] { "Pin", "Tag", "Lines", "Spec" })
        {
            JsonSerializer.Serialize(blanked![name]).Should().Be(JsonSerializer.Serialize(empty![name]),
                $"a protected {name} must look exactly like an empty one — no IsVisible flip, no marker");
        }
    }

    // ---- residual (a): a persistent object in a retry prompt ---------------------------------------

    [Fact]
    public async Task A_retry_prompts_object_is_presented_with_removal_and_redaction()
    {
        var (client, _) = await StartAsync(Rights(denying: ["QueryRead/WrItem/Code"]), "Pin");

        var result = await client.ExecuteActionAsync(ItemTypeId, Prompt);

        result.IsRetry.Should().BeTrue();
        var prompt = result.Retry!.PersistentObject;
        prompt.Should().NotBeNull();
        prompt!.Attributes.Select(a => a.Name).Should().NotContain("Code", "Code is Read-denied").And.Contain("Name");
        prompt["Pin"].Value.Should().BeNull("Pin is protected on this row, whoever filled it");
    }

    // ---- residual (b): a dotted per-row name in an embedded row's own breadcrumb --------------------

    [Fact]
    public async Task A_dotted_protected_name_is_blanked_in_the_embedded_rows_breadcrumb()
    {
        var (client, _) = await StartAsync(Rights(), "Lines.Label");

        var po = await client.GetPersistentObjectAsync(ItemTypeId, "items/1");

        var row = ((PersistentObjectAttributeAsDetail)po!["Lines"]).Objects!.Single();
        row["Label"].Value.Should().BeNull();
        row.Breadcrumb.Should().Be(" x1", "the Label token renders as an empty Label renders");
        // Name is the row type, never a copy of the breadcrumb — so it cannot carry the blanked token.
        row.Name.Should().Be("WrLine");
    }

    // ---- residual (c): nested entity classes -------------------------------------------------------

    [Fact]
    public async Task A_nested_entity_class_is_authorized_by_its_definition_name()
    {
        var (client, _) = await StartAsync(SparkTestSecurity.Empty.Granting("QueryReadEditNew/WrNested"));

        var scaffold = await client.NewPersistentObjectAsync(NestedTypeId);
        Set(scaffold, "Name", "inner");
        var created = await client.CreatePersistentObjectAsync(scaffold);
        var loaded = await client.GetPersistentObjectAsync(NestedTypeId, created.Id!);
        var refreshed = await client.RefreshPersistentObjectAsync(loaded!, "Name");

        loaded!["Name"].Value?.ToString().Should().Be("inner");
        refreshed["Name"].Value?.ToString().Should().Be("inner");
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private async Task<WrItem> LoadAsync(string id)
    {
        using var session = Store.OpenAsyncSession();
        return (await session.LoadAsync<WrItem>(id))!;
    }

    private static IEnumerable<PropertyInfo> WritableProperties(Type type)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite && p.Name != "Id");

    private static string Json(object? value) => JsonSerializer.Serialize(value);

    private static void Set(Po po, string name, object? value)
    {
        var attribute = po.Attributes.FirstOrDefault(a => a.Name == name);
        if (attribute is null)
        {
            attribute = new PersistentObjectAttribute { Name = name, DataType = "string" };
            po.AddAttribute(attribute);
        }
        attribute.Value = value;
        attribute.IsValueChanged = true;
    }

    private static Po NewRow(string label, int qty) => new()
    {
        Name = "WrLine",
        ObjectTypeId = LineTypeId,
        Attributes =
        [
            new PersistentObjectAttribute { Name = "Label", DataType = "string", Value = label, IsValueChanged = true },
            new PersistentObjectAttribute { Name = "Qty", DataType = "number", Value = qty, IsValueChanged = true },
        ],
    };

    /// <summary>
    /// A changed value for <paramref name="attribute"/>, chosen by the CLR type of the property it
    /// maps to — so a new property kind the fixture grows fails here until it is handled. AsDetail
    /// recurses: every row attribute changes and a row is added; a missing single object is created.
    /// </summary>
    private static void Change(PersistentObjectAttribute attribute, Type owner, bool flagged)
    {
        var property = owner.GetProperty(attribute.Name)
            ?? throw new InvalidOperationException($"{owner.Name} has no property {attribute.Name}");
        attribute.IsValueChanged = flagged;
        attribute.IsReadOnly = false;

        if (attribute is PersistentObjectAttributeAsDetail asDetail)
        {
            if (asDetail.IsArray)
            {
                var rowType = property.PropertyType.GetGenericArguments().Single();
                var rows = (asDetail.Objects ?? []).ToList();
                foreach (var row in rows)
                {
                    foreach (var rowAttribute in row.Attributes)
                        Change(rowAttribute, rowType, flagged);
                }
                rows.Add(NewRow("added", 7));
                asDetail.Objects = rows;
            }
            else
            {
                asDetail.Object ??= new Po
                {
                    Name = "WrSpec",
                    ObjectTypeId = SpecTypeId,
                    Attributes =
                    [
                        new PersistentObjectAttribute { Name = "Color", DataType = "string" },
                        new PersistentObjectAttribute { Name = "Size", DataType = "number" },
                    ],
                };
                foreach (var nested in asDetail.Object.Attributes)
                    Change(nested, property.PropertyType, flagged);
            }
            return;
        }

        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        attribute.Value = type switch
        {
            _ when type == typeof(string) && attribute.DataType == "Reference" => "tags/2",
            _ when type == typeof(string) => $"changed-{attribute.Name}",
            _ when type == typeof(bool) => false, // items/1 is stored locked: false is a change
            _ when type == typeof(int) => 99,
            _ when type == typeof(string[]) => new[] { "tags/2" },
            _ when type == typeof(TranslatedString) => new Dictionary<string, string> { ["en"] = "changed" },
            _ => throw new InvalidOperationException($"No changed value for {owner.Name}.{attribute.Name} ({type.Name}): handle the new kind."),
        };
    }

    private static EntityTypeFile ItemModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = ItemTypeId,
            Name = "WrItem",
            ClrType = typeof(WrItem).FullName!,
            Breadcrumb = "{Name}",
            Attributes =
            [
                new() { Id = Guid.NewGuid(), Name = "Name", DataType = "string", Order = 1 },
                new() { Id = Guid.NewGuid(), Name = "Code", DataType = "string", Order = 2 },
                new() { Id = Guid.NewGuid(), Name = "Pin", DataType = "string", Order = 3 },
                new() { Id = Guid.NewGuid(), Name = "Locked", DataType = "boolean", Order = 4 },
                new() { Id = Guid.NewGuid(), Name = "Title", DataType = "TranslatedString", Order = 5 },
                new() { Id = Guid.NewGuid(), Name = "Tag", DataType = "Reference", ReferenceType = typeof(WrTag).FullName, Order = 6 },
                new() { Id = Guid.NewGuid(), Name = "Tags", DataType = "Reference", ReferenceType = typeof(WrTag).FullName, IsArray = true, Order = 7 },
                new() { Id = Guid.NewGuid(), Name = "Lines", DataType = "AsDetail", AsDetailType = typeof(WrLine).FullName, IsArray = true, Order = 8 },
                new() { Id = Guid.NewGuid(), Name = "Spec", DataType = "AsDetail", AsDetailType = typeof(WrSpec).FullName, Order = 9 },
            ],
        },
    };

    private static EntityTypeFile TagModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = TagTypeId,
            Name = "WrTag",
            ClrType = typeof(WrTag).FullName!,
            Breadcrumb = "{Name}",
            Attributes = [new() { Id = Guid.NewGuid(), Name = "Name", DataType = "string" }],
        },
    };

    private static EntityTypeFile LineModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = LineTypeId,
            Name = "WrLine",
            ClrType = typeof(WrLine).FullName!,
            Breadcrumb = "{Label} x{Qty}",
            Attributes =
            [
                new() { Id = Guid.NewGuid(), Name = "Label", DataType = "string", Order = 1 },
                new() { Id = Guid.NewGuid(), Name = "Qty", DataType = "number", Order = 2 },
            ],
        },
    };

    private static EntityTypeFile SpecModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = SpecTypeId,
            Name = "WrSpec",
            ClrType = typeof(WrSpec).FullName!,
            Breadcrumb = "{Color}",
            Attributes =
            [
                new() { Id = Guid.NewGuid(), Name = "Color", DataType = "string", Order = 1 },
                new() { Id = Guid.NewGuid(), Name = "Size", DataType = "number", Order = 2 },
            ],
        },
    };

    private static EntityTypeFile NestedModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = NestedTypeId,
            Name = "WrNested",
            ClrType = typeof(WrOuter.WrNested).FullName!,
            Breadcrumb = "{Name}",
            Attributes = [new() { Id = Guid.NewGuid(), Name = "Name", DataType = "string" }],
        },
    };

    private sealed class StubActionResolver(string name, ICustomAction action) : ICustomActionResolver
    {
        public ICustomAction? Resolve(string requested)
            => string.Equals(requested, name, StringComparison.OrdinalIgnoreCase) ? action : null;

        public IReadOnlyList<string> GetRegisteredActionNames() => [name];
    }
}
