using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Abstractions.Views;
using MintPlayer.Spark.Relational.Schema;
using MintPlayer.Spark.Relational.Storage;
using MintPlayer.Spark.Relational.Tests._Infrastructure;

namespace MintPlayer.Spark.Relational.Tests.Storage;

public class VwCompany
{
    public Id<VwCompany> Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

[IdKind(SparkIdKind.Natural)]
public class VwSkill
{
    public Id<VwSkill> Id { get; set; }
    public string Label { get; set; } = string.Empty;
}

public class VwPerson
{
    public Id<VwPerson> Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public Id<VwCompany>? CompanyId { get; set; }
    public List<Id<VwSkill>> Skills { get; set; } = [];
}

/// <summary>Not stored in the view tests' model: a view over it is left unmapped.</summary>
public class VwElsewhere
{
    public Id<VwElsewhere> Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class VVwPerson
{
    public string? Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string? CompanyName { get; set; }
    public int SkillCount { get; set; }
}

public class VVwName
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

/// <summary>A computed column, a value read through a reference, and a count over a join table (S5's view).</summary>
public sealed class VwPeopleWithCompany : SparkView<VVwPerson>
{
    public override Expression<Func<ISparkViewSource, IQueryable<VVwPerson>>> Map => s =>
        s.Set<VwPerson>().Select(p => new VVwPerson
        {
            Id = s.IdOf(p),
            FullName = p.FirstName + " " + p.LastName,
            CompanyName = s.Load<VwCompany>(p.CompanyId)!.Name,
            SkillCount = p.Skills.Count(),
        });
}

/// <summary>The same view with another definition: the migration drops and recreates it.</summary>
public sealed class VwPeopleWithCompanyV2 : SparkView<VVwPerson>
{
    public override string Name => nameof(VwPeopleWithCompany);

    public override Expression<Func<ISparkViewSource, IQueryable<VVwPerson>>> Map => s =>
        s.Set<VwPerson>().Select(p => new VVwPerson
        {
            Id = s.IdOf(p),
            FullName = p.LastName + ", " + p.FirstName,
            CompanyName = s.Load<VwCompany>(p.CompanyId)!.Name,
            SkillCount = p.Skills.Count(),
        });
}

/// <summary>Captures a field: EF would make it a parameter, which a view body can't have.</summary>
public sealed class VwCaptured : SparkView<VVwName>
{
    private readonly string suffix = " Inc.";

    public override Expression<Func<ISparkViewSource, IQueryable<VVwName>>> Map => s =>
        s.Set<VwCompany>().Select(c => new VVwName { Id = s.IdOf(c), Name = c.Name + suffix });
}

public sealed class VwElsewhereNames : SparkView<VVwName>
{
    public override Expression<Func<ISparkViewSource, IQueryable<VVwName>>> Map => s =>
        s.Set<VwElsewhere>().Select(e => new VVwName { Id = s.IdOf(e), Name = e.Name });
}

/// <summary>
/// <see cref="SparkView{TRow}"/> as an SQL view (#536 M4d, D32): EF writes the SQL, the schema creates the view, a
/// query reads it like a table, and a migration drops and recreates it when it or a table it reads changes.
/// </summary>
public abstract class RelationalViewTests : IAsyncLifetime
{
    private static readonly Type[] Types = [typeof(VwCompany), typeof(VwSkill), typeof(VwPerson)];

    private RelationalTestDatabase? database;

    protected abstract RelationalTestServer? Server { get; }

    private RelationalTestServer RequireServer => Server ?? throw new SkipException("No server configured for this provider (SPARK_TEST_SQLSERVER / SPARK_TEST_POSTGRESQL).");

    public async Task InitializeAsync()
    {
        if (Server is { } server)
            database = await RelationalTestDatabase.CreateAsync(server, Types, views: [new VwPeopleWithCompany()], database: "spark_vw_" + Guid.NewGuid().ToString("N")[..12]);
    }

    public async Task DisposeAsync()
    {
        if (database is not null)
            await database.DisposeAsync();
    }

    private RelationalSparkStore Store => (database ?? throw new SkipException("No server configured for this provider.")).Store;

    private async Task<(VwPerson Ann, VwPerson Bob)> SeedAsync(ISparkStore store)
    {
        using var session = store.OpenSession();
        var acme = new VwCompany { Id = Id<VwCompany>.FromTrusted($"vwcompanies/{Guid.NewGuid():N}"), Name = "Acme" };
        await session.StoreAsync(acme);
        await session.StoreAsync(new VwSkill { Id = Id<VwSkill>.FromTrusted("vwskills/csharp"), Label = "C#" });
        await session.StoreAsync(new VwSkill { Id = Id<VwSkill>.FromTrusted("vwskills/sql"), Label = "SQL" });
        var ann = new VwPerson
        {
            Id = Id<VwPerson>.FromTrusted($"vwpeople/{Guid.NewGuid():N}"),
            FirstName = "Ann",
            LastName = "Lee",
            CompanyId = acme.Id,
            Skills = [Id<VwSkill>.FromTrusted("vwskills/csharp"), Id<VwSkill>.FromTrusted("vwskills/sql")],
        };
        var bob = new VwPerson { Id = Id<VwPerson>.FromTrusted($"vwpeople/{Guid.NewGuid():N}"), FirstName = "Bob", LastName = "Ray" };
        await session.StoreAsync(ann);
        await session.StoreAsync(bob);
        await session.SaveChangesAsync();
        return (ann, bob);
    }

    [SkippableFact]
    public async Task A_view_reads_computed_values_references_and_join_counts()
    {
        var (ann, bob) = await SeedAsync(Store);

        using var session = Store.OpenSession();
        var rows = await session.ToListAsync(session.Query<VVwPerson, VwPeopleWithCompany>().OrderBy(r => r.FullName));

        rows.Should().HaveCount(2);
        rows[0].Id.Should().Be(ann.Id.Value);
        rows[0].FullName.Should().Be("Ann Lee");
        rows[0].CompanyName.Should().Be("Acme");
        rows[0].SkillCount.Should().Be(2);
        rows[1].Id.Should().Be(bob.Id.Value);
        rows[1].CompanyName.Should().BeNull();
        rows[1].SkillCount.Should().Be(0);
    }

    [SkippableFact]
    public async Task A_view_row_is_found_by_its_whole_id()
    {
        var (ann, _) = await SeedAsync(Store);
        var id = ann.Id.Value.ToUpperInvariant();

        using var session = Store.OpenSession();
        var rows = await session.ToListAsync(session.Query<VVwPerson, VwPeopleWithCompany>().Where(r => r.Id == id));

        rows.Should().ContainSingle().Which.FullName.Should().Be("Ann Lee");
    }

    [SkippableFact]
    public async Task The_query_provider_opens_a_view_as_an_index()
    {
        await SeedAsync(Store);

        using var session = Store.OpenSession();
        var provider = new RelationalSparkQueryProvider(session);
        var rows = provider.OpenIndex(typeof(VwPerson), typeof(VwPeopleWithCompany), narrowToCollection: true);

        provider.IsStoreQuery(rows).Should().BeTrue();
        (await provider.CountAsync(rows, CancellationToken.None)).Should().Be(2);
    }

    [SkippableFact]
    public async Task A_changed_view_is_recreated_by_the_migration()
    {
        await SeedAsync(Store);
        var v2 = RelationalTestDatabase.Open(RequireServer, Types, null, Store.NewContext().Database.GetDbConnection().Database, [new VwPeopleWithCompanyV2()]);

        await using (var target = v2.NewContext())
        await using (var source = Store.NewContext())
            await RelationalSchema.ApplyDifferencesAsync(target, source);

        using var session = v2.OpenSession();
        var names = (await session.ToListAsync(session.Query<VVwPerson, VwPeopleWithCompanyV2>())).Select(r => r.FullName);
        names.Should().BeEquivalentTo(["Lee, Ann", "Ray, Bob"]);
    }

    [SkippableFact]
    public async Task A_view_survives_a_change_to_a_column_it_reads()
    {
        await SeedAsync(Store);
        // PostgreSQL refuses to alter a column a view reads: the migration drops the view first and recreates it last.
        var v2 = RelationalTestDatabase.Open(RequireServer, Types,
            mb => mb.Entity<VwPerson>().Property(p => p.FirstName).HasMaxLength(100), Store.NewContext().Database.GetDbConnection().Database,
            [new VwPeopleWithCompany()]);

        await using (var target = v2.NewContext())
        await using (var source = Store.NewContext())
            await RelationalSchema.ApplyDifferencesAsync(target, source);

        using var session = v2.OpenSession();
        (await session.ToListAsync(session.Query<VVwPerson, VwPeopleWithCompany>())).Should().HaveCount(2);
    }

    [SkippableFact]
    public async Task The_view_statement_runs_inside_an_idempotent_script_block()
    {
        await SeedAsync(Store);
        await using var db = Store.NewContext();
        var model = db.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        var create = db.GetService<IMigrationsModelDiffer>().GetDifferences(null, model).OfType<SqlOperation>()
            .Single(o => o.Sql.Contains("CREATE VIEW", StringComparison.Ordinal));
        var statement = db.GetService<IMigrationsSqlGenerator>()
            .Generate([create], db.Model, MigrationsSqlGenerationOptions.Script | MigrationsSqlGenerationOptions.Idempotent)
            .Single().CommandText;
        // The block an idempotent script puts each statement of a migration in (SQL Server: IF … BEGIN; PostgreSQL: DO).
        var block = RequireServer.Dialect is SqlServerSparkDialect
            ? $"IF 1 = 1\nBEGIN\n{statement}\nEND;"
            : $"DO $EF$\nBEGIN\nIF 1 = 1 THEN\n{statement}\nEND IF;\nEND $EF$;";

        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        foreach (var sql in new[] { "DROP VIEW " + db.GetService<ISqlGenerationHelper>().DelimitIdentifier(nameof(VwPeopleWithCompany)), block })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        using var session = Store.OpenSession();
        (await session.ToListAsync(session.Query<VVwPerson, VwPeopleWithCompany>())).Should().HaveCount(2);
    }

    [SkippableFact]
    public void A_view_that_captures_a_variable_is_refused()
    {
        var error = Record.Exception(() => RelationalTestDatabase.Open(RequireServer, Types, null, "spark_vw_never", [new VwCaptured()]));

        error.Should().BeOfType<InvalidOperationException>();
        error!.Message.Should().Contain("VwCaptured").And.Contain("constant");
    }

    [SkippableFact]
    public void A_view_over_a_type_that_is_not_stored_says_so_when_queried()
    {
        var store = RelationalTestDatabase.Open(RequireServer, Types, null, "spark_vw_never", [new VwElsewhereNames()]);

        using var session = store.OpenSession();
        var error = Record.Exception(() => session.Query<VVwName, VwElsewhereNames>());

        error.Should().BeOfType<InvalidOperationException>();
        error!.Message.Should().Contain("VwElsewhere");
    }
}

public sealed class SqlServerViewTests : RelationalViewTests
{
    protected override RelationalTestServer? Server => RelationalTestServer.SqlServer;
}

public sealed class PostgreSqlViewTests : RelationalViewTests
{
    protected override RelationalTestServer? Server => RelationalTestServer.PostgreSql;
}
