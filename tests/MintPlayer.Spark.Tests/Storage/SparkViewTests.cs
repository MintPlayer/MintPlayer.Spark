using System.Linq.Expressions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Abstractions.Views;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Storage.InMemory;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Storage;

/// <summary>
/// #536 D32 / S5: a <see cref="SparkView{TRow}"/> is declared once and rendered by each provider. These cover the
/// declaration, core's view catalog, the in-memory rendering and the index-binding check.
/// </summary>
public class SparkViewTests
{
    public class ViewCompany
    {
        public Id<ViewCompany> Id { get; set; }
        public string Name { get; set; } = "";

        // What the document-id generator emits (D23); this project runs no generator.
        internal string? __DocumentId { get => Id.IsEmpty ? null : Id.Value; set => Id = Id<ViewCompany>.FromTrusted(value); }
    }

    public class ViewEmployee
    {
        public Id<ViewEmployee> Id { get; set; }
        public string FirstName { get; set; } = "";
        public string LastName { get; set; } = "";
        public string? Company { get; set; }
        public Id<ViewCompany>? Employer { get; set; }
        public List<string> Skills { get; set; } = [];
        public ViewAddress? Address { get; set; }
        public Dictionary<string, string>? Labels { get; set; }

        internal string? __DocumentId { get => Id.IsEmpty ? null : Id.Value; set => Id = Id<ViewEmployee>.FromTrusted(value); }
    }

    public class ViewAddress { public string City { get; set; } = ""; }

    public class VEmployeeDetail
    {
        public string? Id { get; set; }
        public string? City { get; set; }
        public string? Dutch { get; set; }
    }

    /// <summary>The shape the generator emits: IdOf for the id, and paths through values that can be absent.</summary>
    public sealed class EmployeeDetails : SparkView<VEmployeeDetail>
    {
        public override Expression<Func<ISparkViewSource, IQueryable<VEmployeeDetail>>> Map => s =>
            from e in s.Set<ViewEmployee>()
            select new VEmployeeDetail { Id = s.IdOf(e), City = e.Address!.City, Dutch = e.Labels!["nl"] };
    }

    public class VEmployee
    {
        public Id<ViewEmployee> Id { get; set; }
        public string FullName { get; set; } = "";
        public string? CompanyName { get; set; }
        public string? EmployerName { get; set; }
        public int SkillCount { get; set; }
    }

    public sealed class EmployeesWithCompany : SparkView<VEmployee>
    {
        public override Expression<Func<ISparkViewSource, IQueryable<VEmployee>>> Map => s =>
            from e in s.Set<ViewEmployee>()
            select new VEmployee
            {
                Id = e.Id,
                FullName = e.FirstName + " " + e.LastName,
                CompanyName = s.Load<ViewCompany>(e.Company)!.Name,
                EmployerName = s.Load<ViewCompany>(e.Employer)!.Name,
                SkillCount = e.Skills.Count,
            };
    }

    [DefaultIndex]
    public sealed class EmployeeNames : SparkView<VEmployee>
    {
        public override string Name => "Employee_Names";

        public override Expression<Func<ISparkViewSource, IQueryable<VEmployee>>> Map => s =>
            s.Set<ViewEmployee>().Select(e => new VEmployee { Id = e.Id, FullName = e.LastName });
    }

    public sealed class NoCollection : SparkView<VEmployee>
    {
        // No public constructor: kept out of an assembly scan, which would refuse it.
        private NoCollection() { }
        public static NoCollection Create() => new();

        public override Expression<Func<ISparkViewSource, IQueryable<VEmployee>>> Map => s => Enumerable.Empty<VEmployee>().AsQueryable();
    }

    [Fact]
    public void The_declaration_names_its_collection_and_every_type_it_reads()
    {
        var view = new EmployeesWithCompany();

        view.Name.Should().Be(nameof(EmployeesWithCompany));
        view.RowType.Should().Be(typeof(VEmployee));
        view.CollectionType.Should().Be(typeof(ViewEmployee));
        view.ReadTypes.Should().Equal(typeof(ViewEmployee), typeof(ViewCompany));
        ((ISparkView)view).Definition.Should().BeSameAs(view.Definition, "the declaration is read once (S5 finding 8)");
    }

    [Fact]
    public async Task InMemory_renders_the_view_with_null_safe_loads()
    {
        var store = new InMemorySparkStore(new InMemoryDatabase());
        using (var seed = store.OpenSession())
        {
            await seed.StoreAsync(new ViewCompany { Name = "Acme" }, "viewcompanies/1");
            await seed.StoreAsync(new ViewEmployee { FirstName = "Ada", LastName = "Lovelace", Company = "ViewCompanies/1", Employer = Id<ViewCompany>.FromTrusted("viewcompanies/1"), Skills = ["math", "poetry"] }, "viewemployees/1");
            await seed.StoreAsync(new ViewEmployee { FirstName = "Alan", LastName = "Turing", Company = "viewcompanies/404" }, "viewemployees/2");
            await seed.SaveChangesAsync();
        }

        using var session = store.OpenSession();
        var rows = new InMemorySparkQueryProvider(session)
            .OpenIndex(typeof(ViewEmployee), typeof(EmployeesWithCompany), narrowToCollection: false)
            .Cast<VEmployee>().OrderBy(r => r.FullName).ToList();

        rows.Should().HaveCount(2);
        rows[0].FullName.Should().Be("Ada Lovelace");
        rows[0].CompanyName.Should().Be("Acme", "a reference id is matched case-insensitively, as ids are");
        rows[0].EmployerName.Should().Be("Acme", "Load takes an Id<T> as well");
        rows[0].SkillCount.Should().Be(2);
        rows[1].FullName.Should().Be("Alan Turing");
        rows[1].CompanyName.Should().BeNull("a missing reference reads as null, as in SQL and RavenDB");
        rows[1].EmployerName.Should().BeNull("an absent Id<T> loads nothing");
    }

    [Fact]
    public async Task InMemory_reads_the_id_and_propagates_absent_values_as_null()
    {
        var store = new InMemorySparkStore(new InMemoryDatabase());
        using (var seed = store.OpenSession())
        {
            await seed.StoreAsync(new ViewEmployee { FirstName = "Ada", Address = new ViewAddress { City = "London" }, Labels = new() { ["nl"] = "Ada NL" } }, "viewemployees/1");
            await seed.StoreAsync(new ViewEmployee { FirstName = "Alan", Labels = new() { ["en"] = "Alan" } }, "viewemployees/2");
            await seed.SaveChangesAsync();
        }

        using var session = store.OpenSession();
        var rows = new InMemorySparkQueryProvider(session)
            .OpenIndex(typeof(ViewEmployee), typeof(EmployeeDetails), narrowToCollection: false)
            .Cast<VEmployeeDetail>().OrderBy(r => r.Id).ToList();

        rows.Select(r => r.Id).Should().Equal("viewemployees/1", "viewemployees/2");
        rows[0].City.Should().Be("London");
        rows[0].Dutch.Should().Be("Ada NL");
        rows[1].City.Should().BeNull("an absent complex value reads as null, as the database propagates it");
        rows[1].Dutch.Should().BeNull("a missing dictionary key reads as null, as RavenDB reads a missing path");
    }

    [Fact]
    public void InMemory_still_refuses_a_native_index()
    {
        using var session = new InMemorySparkStore(new InMemoryDatabase()).OpenSession();

        var open = () => new InMemorySparkQueryProvider(session).OpenIndex(typeof(ViewEmployee), typeof(object), false);

        open.Should().Throw<NotSupportedException>().WithMessage("*SparkView<TRow>*");
    }

    [Fact]
    public void The_view_catalog_enters_a_view_with_its_row_as_projection_and_elects_no_view_it_does_not_claim()
    {
        var sole = new SparkViewCatalog();
        sole.RegisterIndex(typeof(EmployeesWithCompany));
        sole.Freeze();

        var entry = sole.GetByIndexName("employeeswithcompany")!;
        entry.IndexType.Should().Be(typeof(EmployeesWithCompany));
        entry.CollectionType.Should().Be(typeof(ViewEmployee));
        entry.ProjectionType.Should().Be(typeof(VEmployee));
        sole.GetDefaultFor(typeof(ViewEmployee)).Should().BeNull(
            "every view has a row type, so having one chooses nothing: a view is a default only by [DefaultIndex] (#536 D48)");

        var several = new SparkViewCatalog();
        several.RegisterIndex(typeof(EmployeesWithCompany));
        several.RegisterIndex(typeof(EmployeeDetails));
        ((Action)several.Freeze).Should().NotThrow("helper views over one collection are no competing defaults");
        several.GetDefaultFor(typeof(ViewEmployee)).Should().BeNull();
    }

    [Fact]
    public void A_fan_out_view_has_no_row_per_entity()
    {
        var catalog = new SparkViewCatalog();
        catalog.RegisterIndex(typeof(NeutralReduceViewTests.RvFans));
        catalog.Freeze();

        catalog.GetByIndexName(nameof(NeutralReduceViewTests.RvFans))!.CollectionType.Should().BeNull(
            "a row per list item is no row per entity, so the view cannot be the entity type's default (#536 D48, as S16)");
    }

    [Fact]
    public void The_view_catalog_lets_a_claim_win_and_registers_a_type_once()
    {
        var claimed = new SparkViewCatalog();
        claimed.RegisterIndex(typeof(EmployeesWithCompany));
        claimed.RegisterIndex(typeof(EmployeeNames));
        claimed.Freeze();
        claimed.GetDefaultFor(typeof(ViewEmployee))!.IndexName.Should().Be("Employee_Names");

        var twice = new SparkViewCatalog();
        twice.RegisterIndex(typeof(EmployeesWithCompany));
        twice.RegisterIndex(typeof(EmployeesWithCompany));
        twice.GetAllEntries().Should().ContainSingle("the same type through two modules is idempotent");
    }

    [Fact]
    public void The_view_catalog_refuses_a_native_index_and_a_view_without_a_collection()
    {
        var catalog = new SparkViewCatalog();

        ((Action)(() => catalog.RegisterIndex(typeof(string)))).Should().Throw<NotSupportedException>();
        ((Func<Type>)(() => NoCollection.Create().CollectionType)).Should().Throw<InvalidOperationException>().WithMessage("*Set<T>()*");
    }

    [Fact]
    public void A_binding_to_a_view_is_served_by_a_store_without_indexes()
    {
        var loader = Substitute.For<IModelLoader>();
        loader.GetEntityTypes().Returns([new EntityTypeDefinition { Id = Guid.NewGuid(), Name = "Employee", IndexName = "EmployeesWithCompany" }]);
        loader.GetQueries().Returns([new SparkQuery { Id = Guid.NewGuid(), Name = "GetX", Source = "Database.X", IndexName = "Other_Index" }]);
        var store = new InMemorySparkStore(new InMemoryDatabase());

        var unmet = SparkIndexBindingStartupCheck.Unmet(loader, store, SparkViewCatalog.ViewNames([typeof(SparkViewTests).Assembly]));

        unmet.Should().ContainSingle();
        unmet[0].Should().NotContain("EmployeesWithCompany").And.Contain("Other_Index");
    }
}
