using System.Net;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Models;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Authorization;

using Po = Abstractions.PersistentObject;

// Fixture names start with "Wv": ActionsResolver matches actions classes by simple name across the
// whole assembly.

/// <summary>A natural-id entity (id derived from <see cref="Code"/>) with two required attributes.</summary>
public class WvItem : IHasNaturalId
{
    public string? Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Pin { get; set; }
    public bool Locked { get; set; }

    public static string GetId(string code) => $"WvItems/{code}";
    string IHasNaturalId.GetId() => GetId(Code);
}

/// <summary>The per-row hook protects <c>Pin</c> on a locked item.</summary>
public class WvItemActions(IEntityMapper mapper)
    : DefaultPersistentObjectActions<WvItem>(mapper), Abstractions.Authorization.ISparkOwnsRowSecurity
{
    public string RowSecurityRationale => "Test fixture: every row is visible; the subject is attribute rights.";

    public override Task<IReadOnlyCollection<string>?> GetProtectedAttributesAsync(string action, WvItem entity)
        => Task.FromResult<IReadOnlyCollection<string>?>(entity.Locked ? ["Pin"] : null);
}

public class WvContext : SparkContext
{
    public IRavenQueryable<WvItem> Items => Session.Query<WvItem>();
}

/// <summary>
/// Contributions M2d, over HTTP: the natural-id collision probe and save validation only see values
/// the caller may write. The probe used to map the posted object before the write shield, so a value
/// for a <c>New</c>-denied attribute still chose the derived id — rewriting the row that holds it, or
/// answering whether it exists. Validation used to run on the posted values, so a required attribute
/// the caller may not write (blanked per row, or refused by a static <c>Edit</c> right) failed
/// "required" although the save keeps its stored value.
/// </summary>
public class AttributeWriteProbeAndValidationTests : SparkTestDriver
{
    private static readonly Guid ItemTypeId = Guid.Parse("a77b0000-0000-4000-8000-00000000c001");

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

    private async Task<SparkClient> StartAsync(SparkTestSecurity security)
    {
        var factory = new SparkEndpointFactory<WvContext>(
            Store,
            [ItemModel()],
            configureServices: services => services.AddScoped<WvItemActions>(),
            security: security);
        factories.Add(factory);
        var client = new SparkClient(factory.CreateClient(), ownsClient: true);
        clients.Add(client);

        await SeedAsync(async session =>
        {
            await session.StoreAsync(new WvItem { Code = "X", Name = "victim", Pin = "p-x" }, WvItem.GetId("X"));
            await session.StoreAsync(new WvItem { Code = "L", Name = "locked", Pin = "p-stored", Locked = true }, WvItem.GetId("L"));
            await session.StoreAsync(new WvItem { Code = "U", Name = "unlocked", Pin = "p-u" }, WvItem.GetId("U"));
        });

        return client;
    }

    private static SparkTestSecurity Rights(string[] grants, params string[] denying)
        => SparkTestSecurity.Empty.Granting(grants).Denying(denying);

    // ---- 1: the natural-id probe ------------------------------------------------------------------

    /// <summary>
    /// <c>Code</c> derives the id and the caller may not set it on a create. Posting <c>Code = X</c>,
    /// where <c>WvItems/X</c> exists, used to probe with X: with <c>Edit</c> the "create" rewrote that
    /// row, without it the refusal answered "it exists". The value is now dropped before the probe, so
    /// the id derives from the initializer (empty, which RavenDB completes server-side — exactly what
    /// a create that posts no code gets) and the existing row is never touched.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_New_denied_natural_key_cannot_steer_the_derived_id(bool mayEdit)
    {
        var client = await StartAsync(Rights(mayEdit ? ["QueryReadEditNew/WvItem"] : ["QueryRead/WvItem", "New/WvItem"], "New/WvItem/Code"));

        var po = await client.NewPersistentObjectAsync(ItemTypeId);
        Set(po, "Code", "X");
        Set(po, "Name", "attacker");
        Set(po, "Pin", "p-new");

        var created = await client.CreatePersistentObjectAsync(po);

        created.Id.Should().NotBe(WvItem.GetId("X"), "the refused Code must not choose the id");
        var victim = await LoadAsync(WvItem.GetId("X"));
        victim.Name.Should().Be("victim", "the row holding the natural id is untouched");
        victim.Pin.Should().Be("p-x");
        var stored = await LoadAsync(created.Id!);
        stored.Name.Should().Be("attacker");
        stored.Code.Should().Be("", "the field initializer stays, as for any New-denied attribute");
    }

    /// <summary>
    /// A natural id never moves on an update: the base save writes the loaded document under the id
    /// it was loaded with, and RavenDB ids are immutable, so changing the key only changes the field.
    /// </summary>
    [Fact]
    public async Task An_update_that_changes_the_natural_key_keeps_the_document_id()
    {
        var client = await StartAsync(Rights(["QueryReadEditNew/WvItem"]));

        var po = await client.GetPersistentObjectAsync(ItemTypeId, WvItem.GetId("U"));
        Set(po!, "Code", "Z");

        var saved = await client.UpdatePersistentObjectAsync(po!);

        saved.Id.Should().Be(WvItem.GetId("U"));
        (await LoadAsync(WvItem.GetId("U"))).Code.Should().Be("Z");
        using var session = Store.OpenAsyncSession();
        (await session.Advanced.ExistsAsync(WvItem.GetId("Z"))).Should().BeFalse("no second document appears under the new key");
    }

    // ---- 2: validation on the values the save will keep -------------------------------------------

    /// <summary>
    /// <c>Pin</c> is required and the per-row hook protects it on a locked item: the load blanks it and
    /// the client posts the blank back. The save keeps the stored value, so "required" has nothing to
    /// complain about.
    /// </summary>
    [Fact]
    public async Task A_required_attribute_the_row_protects_does_not_fail_validation()
    {
        var client = await StartAsync(Rights(["QueryReadEditNew/WvItem"]));

        var po = await client.GetPersistentObjectAsync(ItemTypeId, WvItem.GetId("L"));
        po!["Pin"].Value.Should().BeNull("the load blanks the protected Pin");
        Set(po, "Name", "renamed");

        var saved = await client.UpdatePersistentObjectAsync(po);

        (saved["Name"].Value?.ToString()).Should().Be("renamed");
        var stored = await LoadAsync(WvItem.GetId("L"));
        stored.Name.Should().Be("renamed");
        stored.Pin.Should().Be("p-stored", "the blank posted back did not wipe the stored value");
    }

    /// <summary>
    /// <c>Code</c> is required and statically <c>Edit</c>-denied: whatever the client posts for it —
    /// here, an empty value — is dropped, so the stored code stays and validation does not judge it.
    /// </summary>
    [Fact]
    public async Task A_required_attribute_a_static_right_refuses_does_not_fail_validation()
    {
        var client = await StartAsync(Rights(["QueryReadEditNew/WvItem"], "Edit/WvItem/Code"));

        var po = await client.GetPersistentObjectAsync(ItemTypeId, WvItem.GetId("U"));
        Set(po!, "Code", "");
        Set(po!, "Name", "renamed");

        await client.UpdatePersistentObjectAsync(po!);

        var stored = await LoadAsync(WvItem.GetId("U"));
        stored.Name.Should().Be("renamed");
        stored.Code.Should().Be("U");
    }

    /// <summary>The attributes the caller may write are still validated.</summary>
    [Fact]
    public async Task A_writable_required_attribute_left_empty_still_fails_validation()
    {
        var client = await StartAsync(Rights(["QueryReadEditNew/WvItem"], "Edit/WvItem/Code"));

        var po = await client.GetPersistentObjectAsync(ItemTypeId, WvItem.GetId("U"));
        Set(po!, "Pin", "");

        var act = () => client.UpdatePersistentObjectAsync(po!);

        (await act.Should().ThrowAsync<SparkClientException>()).Which.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await LoadAsync(WvItem.GetId("U"))).Pin.Should().Be("p-u");
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private async Task<WvItem> LoadAsync(string id)
    {
        using var session = Store.OpenAsyncSession();
        return (await session.LoadAsync<WvItem>(id))!;
    }

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

    private static EntityTypeFile ItemModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = ItemTypeId,
            Name = "WvItem",
            ClrType = typeof(WvItem).FullName!,
            Breadcrumb = "{Name}",
            Attributes =
            [
                new() { Id = Guid.NewGuid(), Name = "Code", DataType = "string", IsRequired = true, Order = 1 },
                new() { Id = Guid.NewGuid(), Name = "Name", DataType = "string", Order = 2 },
                new() { Id = Guid.NewGuid(), Name = "Pin", DataType = "string", IsRequired = true, Order = 3 },
                new() { Id = Guid.NewGuid(), Name = "Locked", DataType = "boolean", Order = 4 },
            ],
        },
    };
}
