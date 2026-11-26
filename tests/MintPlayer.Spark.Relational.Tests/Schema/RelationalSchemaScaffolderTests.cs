using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Relational.Design;
using MintPlayer.Spark.Relational.Model;
using MintPlayer.Spark.Relational.Schema;
using MintPlayer.Spark.Relational.Storage;
using MintPlayer.Spark.Relational.Tests.Storage;
using MintPlayer.Spark.Abstractions.Views;

namespace MintPlayer.Spark.Relational.Tests.Schema;

/// <summary>
/// Synchronize's migration writer (#536 M4c, D24, O17). Scaffolding never opens a connection, so these run without a
/// server, on a connection string that points nowhere.
/// </summary>
public abstract class RelationalSchemaScaffolderTests : IDisposable
{
    private readonly string project = Path.Combine(Path.GetTempPath(), "spark-scaffold-" + Guid.NewGuid().ToString("N")[..8]);

    protected abstract SparkRelationalDialect Dialect { get; }

    protected abstract string NowhereConnectionString { get; }

    private RelationalSparkStore Store(params Type[] types) => Store(types, []);

    private RelationalSparkStore Store(Type[] types, ISparkView[] views)
        => new(new SparkContextSetup(
                SparkRelationalModel.Build(types, t => SparkRelationalExtensions.KindOf(t, new SparkIdOptions()), views), Dialect, []),
            NowhereConnectionString, migrationsAssembly: typeof(RelationalSchemaScaffolderTests).Assembly.GetName().Name);

    /// <summary>
    /// The operations from V1's <paramref name="v1Property"/> to V2's <paramref name="v2Property"/>, columns of one table
    /// (<c>RenPeople</c>); <paramref name="sameAttribute"/> says whether the model gives them one attribute id.
    /// </summary>
    private IReadOnlyList<MigrationOperation> RenameOperations(Type v1, string v1Property, Type v2, string v2Property, bool sameAttribute)
    {
        var id = Guid.NewGuid();
        RelationalSparkStore Versioned(Type type, string property, Guid attribute)
            => new(new SparkContextSetup(
                    SparkRelationalModel.Build([type], t => SparkRelationalExtensions.KindOf(t, new SparkIdOptions())), Dialect,
                    [mb => mb.Entity(type).ToTable("RenPeople")], (t, p) => t == type && p == property ? attribute : null),
                NowhereConnectionString);
        using var before = Versioned(v1, v1Property, id).NewContext();
        using var after = Versioned(v2, v2Property, sameAttribute ? id : Guid.NewGuid()).NewContext();
        return after.GetService<IMigrationsModelDiffer>().GetDifferences(
            before.GetService<IDesignTimeModel>().Model.GetRelationalModel(),
            after.GetService<IDesignTimeModel>().Model.GetRelationalModel());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_renamed_attribute_whose_type_changed_is_renamed_and_altered(bool sameAttribute)
    {
        // V1's Name (text) and V2's Rank (a number). EF pairs columns by name and shape, so on its own a changed type
        // makes a drop and an add, which loses the data (O19). The attribute id decides.
        var operations = RenameOperations(typeof(RenPersonV1), nameof(RenPersonV1.Name), typeof(RenPersonV2), nameof(RenPersonV2.Rank), sameAttribute);

        if (sameAttribute)
        {
            operations.OfType<RenameColumnOperation>().Should().ContainSingle(r => r.Table == "RenPeople" && r.Name == "Name" && r.NewName == "Rank");
            operations.OfType<AlterColumnOperation>().Should().ContainSingle(a => a.Table == "RenPeople" && a.Name == "Rank");
            operations.OfType<DropColumnOperation>().Should().BeEmpty();
            operations.OfType<AddColumnOperation>().Should().BeEmpty();
        }
        else
        {
            operations.OfType<DropColumnOperation>().Should().ContainSingle(d => d.Name == "Name");
            operations.OfType<AddColumnOperation>().Should().ContainSingle(a => a.Name == "Rank");
            operations.OfType<RenameColumnOperation>().Should().BeEmpty();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Two_attributes_of_one_shape_are_paired_only_when_they_are_one_attribute(bool sameAttribute)
    {
        // V1's Name and V3's Title are both text. EF's shape heuristic renames one into the other, which would carry an
        // attribute's data into an unrelated new one; the attribute id decides (O19, the mirror case).
        var operations = RenameOperations(typeof(RenPersonV1), nameof(RenPersonV1.Name), typeof(RenPersonV3), nameof(RenPersonV3.Title), sameAttribute);

        if (sameAttribute)
        {
            operations.OfType<RenameColumnOperation>().Should().ContainSingle(r => r.Name == "Name" && r.NewName == "Title");
            operations.OfType<DropColumnOperation>().Should().BeEmpty();
            operations.OfType<AddColumnOperation>().Should().BeEmpty();
        }
        else
        {
            operations.OfType<RenameColumnOperation>().Should().BeEmpty();
            operations.OfType<DropColumnOperation>().Should().ContainSingle(d => d.Name == "Name");
            operations.OfType<AddColumnOperation>().Should().ContainSingle(a => a.Name == "Title");
        }
    }

    [Fact]
    public void A_view_is_created_last_and_its_SQL_kept_in_the_snapshot()
    {
        var result = RelationalSchemaScaffolder.Scaffold(
            Store([typeof(VwCompany), typeof(VwSkill), typeof(VwPerson)], [new VwPeopleWithCompany()]), new SparkScaffoldRequest(project, "Views"));

        var migration = File.ReadAllText(result.Files[0]);
        var createView = migration.IndexOf("CREATE VIEW", StringComparison.Ordinal);
        createView.Should().BeGreaterThan(migration.LastIndexOf("CreateTable(", StringComparison.Ordinal));
        migration.Should().Contain("VwPeopleWithCompany").And.Contain("FullName").And.Contain("SkillCount");
        var snapshot = File.ReadAllText(result.Files.Single(f => f.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal)));
        snapshot.Should().Contain(SparkMigrationsModelDiffer.ViewSqlAnnotation).And.Contain(SparkMigrationsModelDiffer.ViewTablesAnnotation);
    }

    [Fact]
    public void A_new_model_gets_a_migration_that_creates_its_tables()
    {
        var result = RelationalSchemaScaffolder.Scaffold(Store(typeof(MigPerson)), new SparkScaffoldRequest(project, "Initial"));

        result.Files.Should().HaveCount(3);
        result.Files.Should().OnlyContain(f => File.Exists(f) && f.StartsWith(Path.Combine(project, "Migrations", Dialect.ProviderName), StringComparison.Ordinal));
        result.Operations.Should().Contain("CreateTable MigPeople");
        var code = File.ReadAllText(result.Files[0]);
        code.Should().Contain("namespace MintPlayer.Spark.Relational.Tests.Migrations." + Dialect.ProviderName);
        code.Should().Contain("name: \"MigPeople\"");
        // The framework schema belongs to Spark's own migrations (D24), never to the app's.
        code.Should().NotContain("Reservations");
    }

    [Fact]
    public void The_snapshot_keeps_each_columns_attribute()
    {
        // The next migration is diffed against the snapshot, so the attribute ids must survive it, or a rename made after
        // this migration would pair by name and shape again (O19).
        var attribute = Guid.NewGuid();
        var store = new RelationalSparkStore(new SparkContextSetup(
                SparkRelationalModel.Build([typeof(MigPerson)], t => SparkRelationalExtensions.KindOf(t, new SparkIdOptions())), Dialect, [],
                (t, p) => t == typeof(MigPerson) && p == nameof(MigPerson.Name) ? attribute : null),
            NowhereConnectionString, migrationsAssembly: typeof(RelationalSchemaScaffolderTests).Assembly.GetName().Name);

        var result = RelationalSchemaScaffolder.Scaffold(store, new SparkScaffoldRequest(project, "Initial"));

        var snapshot = File.ReadAllText(result.Files.Single(f => f.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal)));
        snapshot.Should().Contain(Model.SparkDbContext.AttributeIdAnnotation).And.Contain(attribute.ToString("D"));
    }

    [Fact]
    public void A_model_without_tables_writes_nothing()
    {
        RelationalSchemaScaffolder.Scaffold(Store(), new SparkScaffoldRequest(project, null)).Should().Be(SparkScaffoldResult.NoChanges);
        Directory.Exists(project).Should().BeFalse();
    }

    [Fact]
    public void The_context_uses_Sparks_differ()
    {
        // Constructed through EF's service provider: a constructor change in an EF minor fails here, not in production.
        using var db = Store(typeof(MigPerson)).NewContext();
        db.GetService<IMigrationsModelDiffer>().Should().BeOfType<SparkMigrationsModelDiffer>();
    }

    public void Dispose()
    {
        if (Directory.Exists(project))
            Directory.Delete(project, recursive: true);
    }
}

public class RenPersonV1
{
    public Id<RenPersonV1> Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class RenPersonV2
{
    public Id<RenPersonV2> Id { get; set; }
    public int Rank { get; set; }
}

public class RenPersonV3
{
    public Id<RenPersonV3> Id { get; set; }
    public string Title { get; set; } = string.Empty;
}

public sealed class SqlServerSchemaScaffolderTests : RelationalSchemaScaffolderTests
{
    protected override SparkRelationalDialect Dialect { get; } = new SqlServerSparkDialect();

    protected override string NowhereConnectionString => "Server=nowhere.invalid;Database=probe;Integrated Security=true";
}

public sealed class PostgreSqlSchemaScaffolderTests : RelationalSchemaScaffolderTests
{
    protected override SparkRelationalDialect Dialect { get; } = new PostgreSqlSparkDialect();

    protected override string NowhereConnectionString => "Host=nowhere.invalid;Database=probe;Username=probe";
}
