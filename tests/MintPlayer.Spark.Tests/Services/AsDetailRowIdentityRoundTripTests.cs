using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Model;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// The whole cycle, through the save pipeline and the database: create a parent with several
/// embedded rows, read back the keys the server assigned, send an edit quoting those keys, and
/// check the stored document.
/// </summary>
/// <remarks>
/// ⚠️ <b>This exists because the sibling tests are not enough.</b>
/// <see cref="AsDetailStoredRowMergeTests"/> and <see cref="AsDetailRowRightsTests"/> call
/// <c>PopulateObjectValuesAsync</c> and assert on the in-memory entity — they never persist and
/// never re-read. That is one step removed from the shape that gave the abandoned attempt its false
/// confidence: a green suite over a path the real save never takes.
/// <para>
/// So everything here goes through <see cref="IDatabaseAccess.SavePersistentObjectAsync"/>, and every
/// assertion reads the document out of RavenDB rather than inspecting the object the mapper handed
/// back.
/// </para>
/// </remarks>
public class AsDetailRowIdentityRoundTripTests : SparkTestDriver
{
    private static readonly Guid OrderTypeId = Guid.Parse("5e1f0000-0000-4000-8000-5e1f00000001");
    private static readonly Guid LineTypeId = Guid.Parse("5e1f0000-0000-4000-8000-5e1f00000002");

    public class Order
    {
        public string? Id { get; set; }
        public string Reference { get; set; } = string.Empty;
        public List<OrderLine> Lines { get; set; } = [];
    }

    /// <summary>Keyed the way the generator keys a <c>[ValueObject]</c>.</summary>
    public class OrderLine
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Sku { get; set; } = string.Empty;
        public int Quantity { get; set; }

        /// <summary>Server-owned: the model marks it read-only, so a payload may not write it.</summary>
        public string Note { get; set; } = string.Empty;
    }

    private class OrderContext : SparkContext
    {
        public Raven.Client.Documents.Linq.IRavenQueryable<Order> Orders => Session.Query<Order>();
    }

    static AsDetailRowIdentityRoundTripTests()
        => SparkValueObjects.Register(typeof(OrderLine), "Id", row => ((OrderLine)row).Id);

    private static EntityTypeFile OrderModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = OrderTypeId,
            Name = "Order",
            ClrType = typeof(Order).FullName!,
            Breadcrumb = "{Reference}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Reference", DataType = "string" },
                new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(), Name = "Lines", DataType = "AsDetail",
                    AsDetailType = typeof(OrderLine).FullName, IsArray = true,
                },
            ],
        },
    };

    private static EntityTypeFile LineModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = LineTypeId,
            Name = "OrderLine",
            ClrType = typeof(OrderLine).FullName!,
            Breadcrumb = "{Sku}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Sku", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Quantity", DataType = "number" },
                new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(), Name = "Note", DataType = "string", IsReadOnly = true,
                },
            ],
        },
    };

    private SparkEndpointFactory<OrderContext> _factory = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _factory = new SparkEndpointFactory<OrderContext>(Store, [OrderModel(), LineModel()]);
    }

    /// <summary>
    /// One save = one scope = one <c>IAsyncDocumentSession</c>, the way a real request works.
    /// </summary>
    /// <remarks>
    /// ⚠️ Not a formality. Holding a single <c>IDatabaseAccess</c> across the whole test shares one
    /// Raven session, and its identity map hands the update path the instance it loaded during the
    /// create — so a document changed out of band in between is simply invisible. Written that way,
    /// <see cref="A_read_only_field_survives_an_edit_to_its_row"/> failed against correct mapper
    /// code: the merge preserved exactly what its stale <c>existing</c> held.
    /// </remarks>
    private async Task<PersistentObject> SaveAsync(PersistentObject po)
    {
        using var scope = _factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IDatabaseAccess>();
        return await db.SavePersistentObjectAsync(po);
    }

    public override async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    /// <summary>One embedded row on the wire. A null <paramref name="id"/> is a brand-new row.</summary>
    private static PersistentObject Line(string? id, string sku, int quantity)
    {
        var po = new PersistentObject
        {
            Id = id ?? string.Empty,
            ObjectTypeId = LineTypeId,
            Name = "OrderLine",
        };
        po.AddAttribute(new PersistentObjectAttribute { Name = "Sku", DataType = "string", Value = sku, IsValueChanged = true });
        po.AddAttribute(new PersistentObjectAttribute { Name = "Quantity", DataType = "number", Value = quantity, IsValueChanged = true });
        return po;
    }

    private static PersistentObject OrderPo(string? id, string reference, params PersistentObject[] lines)
    {
        var po = new PersistentObject { Id = id, ObjectTypeId = OrderTypeId, Name = "Order" };
        po.AddAttribute(new PersistentObjectAttribute { Name = "Reference", DataType = "string", Value = reference, IsValueChanged = true });
        po.AddAttribute(new PersistentObjectAttributeAsDetail
        {
            Name = "Lines",
            DataType = "AsDetail",
            IsArray = true,
            AsDetailType = typeof(OrderLine).FullName,
            Objects = [.. lines],
            IsValueChanged = true,
        });
        return po;
    }

    /// <summary>Reads the stored document, never the object the save returned.</summary>
    private async Task<Order> FromDatabaseAsync(string id)
    {
        using var session = Store.OpenAsyncSession();
        var order = await session.LoadAsync<Order>(id);
        order.Should().NotBeNull($"'{id}' should exist in the database");
        return order!;
    }

    [Fact]
    public async Task Creating_an_order_gives_every_line_a_distinct_key()
    {
        var saved = await SaveAsync(
            OrderPo(null, "ORD-1", Line(null, "apple", 1), Line(null, "pear", 2), Line(null, "plum", 3)));

        var stored = await FromDatabaseAsync(saved.Id!);

        stored.Lines.Should().HaveCount(3);
        stored.Lines.Select(l => l.Id).Should().OnlyHaveUniqueItems();
        stored.Lines.Select(l => l.Id).Should().AllSatisfy(k => k.Should().NotBeNullOrEmpty());
        stored.Lines.Select(l => l.Sku).Should().Equal("apple", "pear", "plum");
    }

    /// <summary>
    /// The question the whole design exists to answer: edit one row, quoting the keys the server
    /// assigned, and every key survives.
    /// </summary>
    [Fact]
    public async Task Editing_lines_preserves_every_key_and_the_edited_values()
    {
        var created = await SaveAsync(
            OrderPo(null, "ORD-2", Line(null, "apple", 1), Line(null, "pear", 2), Line(null, "plum", 3)));

        var before = await FromDatabaseAsync(created.Id!);
        var keys = before.Lines.Select(l => l.Id).ToArray();

        // What the client sends back: the same rows, same keys, one value changed.
        await SaveAsync(OrderPo(created.Id, "ORD-2",
            Line(keys[0], "apple", 1),
            Line(keys[1], "pear", 99),
            Line(keys[2], "plum", 3)));

        var after = await FromDatabaseAsync(created.Id!);

        after.Lines.Select(l => l.Id).Should().Equal(keys, "a matched row keeps the key it was given");
        after.Lines.Select(l => l.Quantity).Should().Equal(1, 99, 3);
        after.Lines.Select(l => l.Sku).Should().Equal("apple", "pear", "plum");
    }

    /// <summary>
    /// The data-loss bug, end to end. The payload may not write <c>Note</c>, and on a rebuilt row
    /// there would be no stored value left to prefer.
    /// </summary>
    [Fact]
    public async Task A_read_only_field_survives_an_edit_to_its_row()
    {
        var created = await SaveAsync(
            OrderPo(null, "ORD-3", Line(null, "apple", 1), Line(null, "pear", 2)));

        // Stamp the server-owned field the way a background process would.
        using (var session = Store.OpenAsyncSession())
        {
            var order = await session.LoadAsync<Order>(created.Id);
            order.Lines[0].Note = "restocked 2026-09-09";
            order.Lines[1].Note = "backordered";
            await session.SaveChangesAsync();
        }

        var before = await FromDatabaseAsync(created.Id!);
        var keys = before.Lines.Select(l => l.Id).ToArray();

        // Guard the fixture itself: if the stamp did not persist, the real assertion below would
        // fail for a reason that has nothing to do with the merge.
        before.Lines[0].Note.Should().Be("restocked 2026-09-09", "the fixture must have stamped it");

        // The client edits a quantity and — as a browser would — sends no Note at all.
        await SaveAsync(OrderPo(created.Id, "ORD-3",
            Line(keys[0], "apple", 42),
            Line(keys[1], "pear", 2)));

        var after = await FromDatabaseAsync(created.Id!);

        after.Lines[0].Quantity.Should().Be(42);
        after.Lines[0].Note.Should().Be("restocked 2026-09-09",
            "the row was populated onto its stored instance, so a field the payload may not write "
            + "was never lost to begin with");
        after.Lines[1].Note.Should().Be("backordered");
    }

    /// <summary>
    /// A row the client sends without a key is new, and gets one — while the existing rows keep
    /// theirs.
    /// </summary>
    [Fact]
    public async Task Adding_a_line_keys_only_the_new_one()
    {
        var created = await SaveAsync(
            OrderPo(null, "ORD-4", Line(null, "apple", 1), Line(null, "pear", 2)));

        var keys = (await FromDatabaseAsync(created.Id!)).Lines.Select(l => l.Id).ToArray();

        await SaveAsync(OrderPo(created.Id, "ORD-4",
            Line(keys[0], "apple", 1), Line(keys[1], "pear", 2), Line(null, "plum", 7)));

        var after = await FromDatabaseAsync(created.Id!);

        after.Lines.Should().HaveCount(3);
        after.Lines.Take(2).Select(l => l.Id).Should().Equal(keys);
        after.Lines[2].Id.Should().NotBeNullOrEmpty();
        keys.Should().NotContain(after.Lines[2].Id, "a new row gets a key of its own");
        after.Lines[2].Sku.Should().Be("plum");
    }

    [Fact]
    public async Task Removing_a_line_leaves_the_others_untouched()
    {
        var created = await SaveAsync(
            OrderPo(null, "ORD-5", Line(null, "apple", 1), Line(null, "pear", 2), Line(null, "plum", 3)));

        var keys = (await FromDatabaseAsync(created.Id!)).Lines.Select(l => l.Id).ToArray();

        await SaveAsync(OrderPo(created.Id, "ORD-5",
            Line(keys[0], "apple", 1), Line(keys[2], "plum", 3)));

        var after = await FromDatabaseAsync(created.Id!);

        after.Lines.Select(l => l.Id).Should().Equal(keys[0], keys[2]);
        after.Lines.Select(l => l.Sku).Should().Equal("apple", "plum");
    }

    /// <summary>
    /// Reordering is not a rename. Row identity travels with the key, not the position.
    /// </summary>
    [Fact]
    public async Task Reordering_lines_moves_the_keys_with_them()
    {
        var created = await SaveAsync(
            OrderPo(null, "ORD-6", Line(null, "apple", 1), Line(null, "pear", 2)));

        var keys = (await FromDatabaseAsync(created.Id!)).Lines.Select(l => l.Id).ToArray();

        await SaveAsync(OrderPo(created.Id, "ORD-6",
            Line(keys[1], "pear", 2), Line(keys[0], "apple", 1)));

        var after = await FromDatabaseAsync(created.Id!);

        after.Lines.Select(l => l.Sku).Should().Equal("pear", "apple");
        after.Lines.Select(l => l.Id).Should().Equal(keys[1], keys[0]);
    }

    /// <summary>
    /// Saving the same payload twice must not churn identity — the property the browser check
    /// measured as an unchanged change vector.
    /// </summary>
    [Fact]
    public async Task Saving_an_unchanged_collection_twice_changes_no_key()
    {
        var created = await SaveAsync(
            OrderPo(null, "ORD-7", Line(null, "apple", 1), Line(null, "pear", 2)));

        var keys = (await FromDatabaseAsync(created.Id!)).Lines.Select(l => l.Id).ToArray();

        var unchanged = () => OrderPo(created.Id, "ORD-7", Line(keys[0], "apple", 1), Line(keys[1], "pear", 2));
        await SaveAsync(unchanged());
        await SaveAsync(unchanged());

        (await FromDatabaseAsync(created.Id!)).Lines.Select(l => l.Id).Should().Equal(keys);
    }
}
