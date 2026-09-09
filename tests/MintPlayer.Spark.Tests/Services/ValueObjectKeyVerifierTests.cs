using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Model;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// The startup gate: a keyless stored row must stop the application, because nothing downstream can
/// notice it.
/// </summary>
/// <remarks>
/// A keyless row deserializes with a freshly minted key, so in memory it looks correct — see
/// <see cref="NestedRowIdentityTests"/>. Only the raw JSON knows, so the gate queries the database.
/// It also cannot warn and continue: loading such a document marks it dirty, so the next unrelated
/// save writes random ids into a document nobody edited.
/// </remarks>
public class ValueObjectKeyVerifierTests : SparkTestDriver
{
    public class Order
    {
        public string? Id { get; set; }
        public List<Line> Lines { get; set; } = [];
        public Shipping Shipping { get; set; } = new();
    }

    public class Line
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Sku { get; set; } = string.Empty;
    }

    /// <summary>A single nested object: no key of its own, but its collection still counts.</summary>
    public class Shipping
    {
        public string Carrier { get; set; } = string.Empty;
        public List<Leg> Legs { get; set; } = [];
    }

    public class Leg
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string From { get; set; } = string.Empty;
    }

    /// <summary>Not registered, so the gate must not ask about it.</summary>
    public class Note
    {
        public string Text { get; set; } = string.Empty;
    }

    private class TestContext : SparkContext
    {
        public IRavenQueryable<Order> Orders => throw new NotSupportedException("shape only");
    }

    static ValueObjectKeyVerifierTests()
    {
        SparkValueObjects.Register(typeof(Line), "Id", row => ((Line)row).Id);
        SparkValueObjects.Register(typeof(Leg), "Id", row => ((Leg)row).Id);
    }

    // Direct, not reflected: MintPlayer.Spark grants InternalsVisibleTo to this assembly, and a
    // reflected call would keep compiling after the method was renamed or its signature changed.
    private static Task VerifyAsync(IDocumentStore store)
        => MintPlayer.Spark.Services.ValueObjectKeyVerifier.VerifyAsync(
            typeof(TestContext), store, _ => { }, CancellationToken.None);

    private static async Task StripKeysAsync(IDocumentStore store, string arrayPath)
    {
        var operation = await store.Operations.SendAsync(new PatchByQueryOperation(new IndexQuery
        {
            Query = $$"""
                from Orders as o update {
                    var rows = o.{{arrayPath}};
                    if (rows) { for (var i = 0; i < rows.length; i++) { delete rows[i].Id; } }
                }
                """,
        }));
        await operation.WaitForCompletionAsync(TimeSpan.FromMinutes(1));
    }

    private static async Task SeedAsync(IDocumentStore store)
    {
        using var session = store.OpenAsyncSession();
        await session.StoreAsync(new Order
        {
            Lines = [new Line { Sku = "a" }, new Line { Sku = "b" }],
            Shipping = new Shipping { Carrier = "dhl", Legs = [new Leg { From = "BRU" }] },
        }, "Orders/1-A");
        await session.SaveChangesAsync();
    }

    [Fact]
    public async Task Fully_keyed_data_passes()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);

        var act = () => VerifyAsync(store);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_keyless_row_refuses_startup()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        await StripKeysAsync(store, "Lines");

        var act = () => VerifyAsync(store);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*Orders.Lines*");
    }

    /// <summary>
    /// A collection below a single nested object is as much part of the model as one at the top, so
    /// the walk has to descend through the object even though the object itself needs no key.
    /// </summary>
    [Fact]
    public async Task A_keyless_row_nested_below_a_single_object_is_found()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);
        await StripKeysAsync(store, "Shipping.Legs");

        var act = () => VerifyAsync(store);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*Shipping.Legs*");
    }

    /// <summary>
    /// An empty key is as unmatchable as an absent one, and is the shape a type whose key defaults
    /// to <c>string.Empty</c> leaves behind.
    /// </summary>
    [Fact]
    public async Task An_empty_key_counts_as_missing()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store);

        var operation = await store.Operations.SendAsync(new PatchByQueryOperation(new IndexQuery
        {
            Query = "from Orders as o update { o.Lines[0].Id = ''; }",
        }));
        await operation.WaitForCompletionAsync(TimeSpan.FromMinutes(1));

        var act = () => VerifyAsync(store);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>
    /// An empty database is not a failure — a fresh deployment has nothing to migrate.
    /// </summary>
    [Fact]
    public async Task An_empty_database_passes()
    {
        using var store = GetDocumentStore();

        var act = () => VerifyAsync(store);

        await act.Should().NotThrowAsync();
    }
}
