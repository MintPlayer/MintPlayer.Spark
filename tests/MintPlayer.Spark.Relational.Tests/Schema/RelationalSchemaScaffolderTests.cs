using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Relational.Design;
using MintPlayer.Spark.Relational.Model;
using MintPlayer.Spark.Relational.Schema;
using MintPlayer.Spark.Relational.Storage;

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

    private RelationalSparkStore Store(params Type[] types)
        => new(new SparkContextSetup(
                SparkRelationalModel.Build(types, t => SparkRelationalExtensions.KindOf(t, new SparkIdOptions())), Dialect, []),
            NowhereConnectionString, migrationsAssembly: typeof(RelationalSchemaScaffolderTests).Assembly.GetName().Name);

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
