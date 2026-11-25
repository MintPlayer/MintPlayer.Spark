using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_renamed_attribute_is_not_dropped_and_added(bool sameAttribute)
    {
        // V1's Name (text) and V2's Rank (a number) are columns of one table; the model says whether they are one
        // attribute. EF renames a column itself when only the name changed; a changed type too makes a drop and an add.
        var name = Guid.NewGuid();
        RelationalSparkStore Versioned(Type type, string property, Guid id)
            => new(new SparkContextSetup(
                    SparkRelationalModel.Build([type], t => SparkRelationalExtensions.KindOf(t, new SparkIdOptions())), Dialect,
                    [mb => mb.Entity(type).ToTable("RenPeople")], (t, p) => t == type && p == property ? id : null),
                NowhereConnectionString);
        using var v1 = Versioned(typeof(RenPersonV1), nameof(RenPersonV1.Name), name).NewContext();
        using var v2 = Versioned(typeof(RenPersonV2), nameof(RenPersonV2.Rank), sameAttribute ? name : Guid.NewGuid()).NewContext();
        var previous = v1.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        var target = v2.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        var operations = v2.GetService<IMigrationsModelDiffer>().GetDifferences(previous, target);

        var refusal = Record.Exception(() => RelationalSchemaScaffolder.RefuseSplitAttributes(previous, target, operations));

        if (sameAttribute)
            refusal.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("RenPeople.Name -> Rank");
        else
            refusal.Should().BeNull();
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
