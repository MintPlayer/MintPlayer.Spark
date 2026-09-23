using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Queries;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// SP4: free-text search over a custom query that goes through a STATIC index.
/// </summary>
public class StaticIndexSearchTests : SparkTestDriver
{
    private static readonly Guid GadgetTypeId = Guid.Parse("cccc4444-cccc-cccc-cccc-cccc44444444");

    public class Sp4Gadget
    {
        public string? Id { get; set; }
        public string Label { get; set; } = string.Empty;
        public string Owner { get; set; } = string.Empty;

        /// <summary>Deliberately NOT mapped by the index below — the [IgnoreForIndex] shape.</summary>
        public string? SecretToken { get; set; }
    }

    /// <summary>Distinctive prefix: index class names must be unique across the whole assembly.</summary>
    public class Sp4Gadgets_Overview : AbstractIndexCreationTask<Sp4Gadget>
    {
        public Sp4Gadgets_Overview()
        {
            Map = gadgets => from g in gadgets select new { g.Label, g.Owner };
            StoreAllFields(FieldStorage.Yes);
        }
    }

    public class Sp4GadgetActions : DefaultPersistentObjectActions<Sp4Gadget>
    {
        private readonly IAsyncDocumentSession _session;
        public Sp4GadgetActions(IEntityMapper entityMapper, IAsyncDocumentSession session) : base(entityMapper)
            => _session = session;

        /// <summary>The production shape: parent-scoped custom query THROUGH a static index.</summary>
        public IRavenQueryable<Sp4Gadget> GadgetsOfOwner(CustomQueryArgs args)
            => _session.Query<Sp4Gadget, Sp4Gadgets_Overview>().Where(g => g.Owner == args.Parent!.Id);

        /// <summary>Control: the same query with NO static index (auto-index).</summary>
        public IRavenQueryable<Sp4Gadget> GadgetsOfOwnerNoIndex(CustomQueryArgs args)
            => _session.Query<Sp4Gadget>().Where(g => g.Owner == args.Parent!.Id);
    }

    public class Sp4Context : SparkContext
    {
        public IRavenQueryable<Sp4Gadget> Gadgets => Session.Query<Sp4Gadget>();
    }

    private static EntityTypeFile GadgetModel(bool bindIndex = false) => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = GadgetTypeId,
            Name = "Sp4Gadget",
            ClrType = typeof(Sp4Gadget).FullName!,
            IndexName = bindIndex ? "Sp4Gadgets_Overview" : null,
            Breadcrumb = "{Label}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = nameof(Sp4Gadget.Label), DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = nameof(Sp4Gadget.Owner), DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = nameof(Sp4Gadget.SecretToken), DataType = "string" },
            ],
        },
    };

    private SparkEndpointFactory<Sp4Context>? _factory;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        await SeedAsync(async session =>
        {
            await session.StoreAsync(new Sp4Gadget { Label = "alpha widget", Owner = "owners/1", SecretToken = "tok-a" });
            await session.StoreAsync(new Sp4Gadget { Label = "beta widget", Owner = "owners/1", SecretToken = "tok-b" });
            await session.StoreAsync(new Sp4Gadget { Label = "gamma widget", Owner = "owners/2", SecretToken = "tok-c" });
        });

        await new Sp4Gadgets_Overview().ExecuteAsync(Store);
        await RavenIndexHelper.WaitForNonStaleAsync(Store);
    }

    public override async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private IQueryExecutor Executor(bool bindIndex = false)
    {
        _factory = new SparkEndpointFactory<Sp4Context>(Store, [GadgetModel(bindIndex)],
            configureIndexCatalog: catalog => catalog.RegisterIndex(typeof(Sp4Gadgets_Overview)));
        return _factory.GetService<IQueryExecutor>();
    }

    /// <summary>Database.* bound to the SAME projection-less index — does it fail the same way?</summary>
    [Fact]
    public async Task Search_on_a_database_query_bound_to_a_projectionless_index()
    {
        var executor = Executor(bindIndex: true);

        var q = new SparkQuery
        {
            Id = Guid.NewGuid(),
            Name = "GadgetsDb",
            Source = "Database.Gadgets",
            IndexName = "Sp4Gadgets_Overview",
        };

        var ex = await Record.ExceptionAsync(async () => await executor.ExecuteQueryAsync(q, search: "alpha"));

        File.WriteAllText(@"C:\Users\piete\AppData\Local\Temp\claude\C--Repos-MintPlayer-Spark\3d266f03-c5a4-4658-9cae-b2be0a7b6c3a\scratchpad\sp4-database.txt",
            (ex?.GetType().FullName ?? "<none>") + "\n\n" + (ex?.Message ?? "<no exception thrown>"));
    }

    private static SparkQuery CustomQuery(string method) => new()
    {
        Id = Guid.NewGuid(),
        Name = method,
        Source = $"Custom.{method}",
        EntityType = "Sp4Gadget",
    };

    private static PersistentObject Parent() =>
        new() { Id = "owners/1", Name = "Owner", ObjectTypeId = Guid.NewGuid() };

    [Fact]
    public async Task Search_through_a_static_index_reproduces_the_production_failure()
    {
        var executor = Executor();

        var act = async () => await executor.ExecuteQueryAsync(
            CustomQuery("GadgetsOfOwner"), Parent(), search: "alpha");

        var ex = await Record.ExceptionAsync(act);

        // Print everything, verbatim, for the report.
        File.WriteAllText(@"C:\Users\piete\AppData\Local\Temp\claude\C--Repos-MintPlayer-Spark\3d266f03-c5a4-4658-9cae-b2be0a7b6c3a\scratchpad\sp4-exception.txt",
            (ex?.GetType().FullName ?? "<none>") + "\n\n" + (ex?.ToString() ?? "<no exception thrown>"));

        ex.Should().NotBeNull("SP4 reports a deterministic 500 for any search term");
    }

    [Fact]
    public void Probe_what_the_executor_can_learn_about_a_custom_queryables_index()
    {
        using var session = Store.OpenAsyncSession();
        object q = session.Query<Sp4Gadget, Sp4Gadgets_Overview>().Where(g => g.Owner == "owners/1");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("runtime type: " + q.GetType().FullName);
        foreach (var i in q.GetType().GetInterfaces()) sb.AppendLine("  iface: " + i.FullName);
        foreach (var m in q.GetType().GetMembers())
            if (m.Name.Contains("Index", StringComparison.OrdinalIgnoreCase)) sb.AppendLine("  member: " + m);

        // Does a public API hand back the index name?
        var inspector = q.GetType().GetInterfaces()
            .FirstOrDefault(i => i.Name.Contains("Inspector", StringComparison.Ordinal));
        sb.AppendLine("inspector iface: " + (inspector?.FullName ?? "<none>"));
        if (inspector is not null)
            foreach (var m in inspector.GetMembers()) sb.AppendLine("  inspector member: " + m);

        File.WriteAllText(@"C:\Users\piete\AppData\Local\Temp\claude\C--Repos-MintPlayer-Spark\3d266f03-c5a4-4658-9cae-b2be0a7b6c3a\scratchpad\sp4-probe.txt", sb.ToString());
    }

    [Fact]
    public async Task Search_without_a_static_index_narrows_correctly()
    {
        var executor = Executor();

        var result = await executor.ExecuteQueryAsync(
            CustomQuery("GadgetsOfOwnerNoIndex"), Parent(), search: "alpha");

        Console.WriteLine($"=== SP4 CONTROL: {result.TotalItems} rows ===");
        result.TotalItems.Should().Be(1);
    }
}
