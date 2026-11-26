using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Relational.Tests._Infrastructure;

namespace MintPlayer.Spark.Relational.Tests.Storage;

public class RelCompany
{
    public Id<RelCompany> Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

[IdKind(SparkIdKind.Natural)]
public class RelSkill
{
    public Id<RelSkill> Id { get; set; }
    public string Label { get; set; } = string.Empty;
}

[ValueObject]
public class RelJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public DateOnly Start { get; set; }

    /// <summary>O13 in a child table. The field is what <c>StoredGetterGenerator</c> emits; this project doesn't run it.</summary>
    public string Label => Title.ToUpperInvariant();
#pragma warning disable CS0169
    private string? __stored_Label;
#pragma warning restore CS0169
}

/// <summary>A second child table on <see cref="RelPerson"/>: two AsDetail collections make its reads split (PRD §8.10, finding 5).</summary>
[ValueObject]
public class RelCourse
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
}

public enum RelLevel { Junior, Medior, Senior }

[Flags]
public enum RelRights { None = 0, Read = 1, Write = 2 }

public class RelAddress
{
    public string Street { get; set; } = string.Empty;
    public int Number { get; set; }
    public RelLevel Level { get; set; }
}

public class RelPerson
{
    public Id<RelPerson> Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
    public RelLevel Level { get; set; }
    public RelLevel? NextLevel { get; set; }
    public List<RelLevel> PastLevels { get; set; } = [];
    public RelRights Rights { get; set; }
    public Id<RelCompany>? CompanyId { get; set; }
    public List<Id<RelSkill>> Skills { get; set; } = [];
    public List<RelJob> Jobs { get; set; } = [];
    public List<RelCourse> Courses { get; set; } = [];
    public RelAddress? Address { get; set; }
    public TranslatedString? Title { get; set; }
    public List<string> Tags { get; set; } = [];

    // O13: stored getters. The fields are what StoredGetterGenerator emits; this project doesn't run it.
    public string Display => $"{Name} ({Age})";
    public bool Adult => Age >= 18;
    public RelLevel Band => Age >= 40 ? RelLevel.Senior : RelLevel.Junior;
    public List<string> Initials => Name.Length == 0 ? [] : [Name[..1]];
    public RelAddress? Home => Address is null ? null : new RelAddress { Street = Address.Street.ToUpperInvariant(), Number = Address.Number };
    /// <summary>No backing field: not stored on SQL.</summary>
    public string Unstored => Name;
    /// <summary>A backing field, but <c>[JsonIgnore]</c>: the document doesn't hold it, so neither does the table.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string Ignored => Name;
#pragma warning disable CS0169
    private string? __stored_Display;
    private bool __stored_Adult;
    private RelLevel __stored_Band;
    private List<string>? __stored_Initials;
    private RelAddress? __stored_Home;
    private string? __stored_Ignored;
#pragma warning restore CS0169
}

/// <summary>A whole-text id (a <see cref="string"/> <c>Id</c>), as documents written before #536 M2 have.</summary>
public class RelNote
{
    public string? Id { get; set; }
    public string Text { get; set; } = string.Empty;
}

/// <summary>
/// The relational store's unit of work (#536 M4a), as S2b and S4 proved it on the spike: values, child and join tables,
/// versions over the aggregate (D3), document-PUT stores, deletes, lowercase ids and escaped prefixes (D7), and
/// constraint errors as validation messages (D30). Runs once per configured server.
/// </summary>
public abstract class RelationalStoreTests : IAsyncLifetime
{
    private RelationalTestDatabase? database;

    /// <summary>The text of every statement that reads rows, in order.</summary>
    private readonly CommandLog commands = new();

    protected abstract RelationalTestServer? Server { get; }

    protected ISparkStore Store => database!.Store;

    public async Task InitializeAsync()
    {
        if (Server is { } server)
            database = await RelationalTestDatabase.CreateAsync(
                server, [typeof(RelCompany), typeof(RelSkill), typeof(RelPerson), typeof(RelNote)],
                configureContext: options => options.AddInterceptors(commands));
    }

    private sealed class CommandLog : DbCommandInterceptor
    {
        public List<string> Reads { get; } = [];

        public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            lock (Reads)
                Reads.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<System.Data.Common.DbDataReader> ReaderExecuting(
            System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<System.Data.Common.DbDataReader> result)
        {
            lock (Reads)
                Reads.Add(command.CommandText);
            return result;
        }
    }

    public async Task DisposeAsync()
    {
        if (database is not null)
            await database.DisposeAsync();
    }

    private void RequireServer() => Skip.If(database is null, "No server configured for this provider (SPARK_TEST_SQLSERVER / SPARK_TEST_POSTGRESQL).");

    private static Id<T> NewId<T>(string collection) => Id<T>.FromTrusted($"{collection}/{Guid.NewGuid():N}");

    private static Id<RelSkill> Skill(string key) => Id<RelSkill>.FromTrusted($"relskills/{key}");

    private async Task<(RelCompany Company, RelPerson Person)> SeedAsync()
    {
        using var session = Store.OpenSession();
        var company = new RelCompany { Id = NewId<RelCompany>("relcompanies"), Name = "Acme" };
        await session.StoreAsync(company);
        await session.StoreAsync(new RelSkill { Id = Skill("csharp"), Label = "C#" });
        await session.StoreAsync(new RelSkill { Id = Skill("sql"), Label = "SQL" });
        var person = new RelPerson
        {
            Id = NewId<RelPerson>("relpeople"),
            Name = "Ann",
            Age = 41,
            CompanyId = company.Id,
            Skills = [Skill("csharp"), Skill("sql")],
            Jobs = [new RelJob { Title = "Developer", Start = new DateOnly(2020, 1, 1) }],
            Address = new RelAddress { Street = "Main", Number = 7 },
            Title = TranslatedString.Create("Engineer", "nl", "Ingenieur"),
            Tags = ["a", "b"],
        };
        await session.StoreAsync(person);
        await session.SaveChangesAsync();
        return (company, person);
    }

    [SkippableFact]
    public async Task An_aggregate_round_trips_through_a_new_session()
    {
        RequireServer();
        var (company, person) = await SeedAsync();

        using var session = Store.OpenSession();
        var loaded = await session.LoadAsync<RelPerson>(person.Id.Value);

        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("Ann");
        loaded.Age.Should().Be(41);
        loaded.CompanyId.Should().Be(company.Id);
        loaded.Skills.Should().BeEquivalentTo([Skill("csharp"), Skill("sql")]);
        loaded.Jobs.Should().HaveCount(1);
        loaded.Jobs[0].Title.Should().Be("Developer");
        loaded.Jobs[0].Start.Should().Be(new DateOnly(2020, 1, 1));
        loaded.Address!.Street.Should().Be("Main");
        loaded.Address.Number.Should().Be(7);
        loaded.Title!.GetValue("nl").Should().Be("Ingenieur");
        loaded.Tags.Should().BeEquivalentTo(["a", "b"]);
        session.GetVersion(loaded).Should().Be("1");
    }

    /// <summary>
    /// O13: a get-only member with a generated backing field is a column, written from the getter on every save (here a
    /// change to <c>Name</c> rewrites <c>Display</c>), in a child table too, and LINQ on it filters and sorts in SQL. A
    /// getter without the field has no column.
    /// </summary>
    [SkippableFact]
    public async Task A_stored_getter_is_written_on_every_save_and_queries_as_its_column()
    {
        RequireServer();
        var (_, person) = await SeedAsync();
        (await ReadStoredGettersAsync()).Should().Be(("Ann (41)", true, "DEVELOPER"));

        using (var session = Store.OpenSession())
        {
            var stored = (await session.LoadAsync<RelPerson>(person.Id.Value))!;
            stored.Name = "Bob";
            stored.Jobs[0].Title = "Lead";
            await session.StoreAsync(new RelPerson { Id = NewId<RelPerson>("relpeople"), Name = "Cy", Age = 9 });
            await session.SaveChangesAsync();
        }

        (await ReadStoredGettersAsync()).Should().Be(("Bob (41)", true, "LEAD"));
        await using (var db = database!.Store.NewContext())
        {
            var people = db.Model.FindEntityType(typeof(RelPerson))!;
            people.FindProperty(nameof(RelPerson.Unstored)).Should().BeNull();
            people.FindProperty(nameof(RelPerson.Ignored)).Should().BeNull();
        }

        using (var session = Store.OpenSession())
        {
            (await session.CountAsync(session.Query<RelPerson>().Where(p => p.Display == "bob (41)"))).Should().Be(1); // O20: text ignores case
            (await session.CountAsync(session.Query<RelPerson>().Where(p => p.Adult))).Should().Be(1);
            (await session.CountAsync(session.Query<RelPerson>().Where(p => p.Band == RelLevel.Senior))).Should().Be(1);
            (await session.CountAsync(session.Query<RelPerson>().Where(p => p.Initials.Contains("B")))).Should().Be(1);
            var bob = (await session.LoadAsync<RelPerson>(person.Id.Value))!;
            bob.Home!.Street.Should().Be("MAIN");
            var sorted = await session.ToListAsync(session.Query<RelPerson>().OrderByDescending(p => p.Display));
            sorted.Select(p => p.Display).Should().Equal("Cy (9)", "Bob (41)");
        }
    }

    private async Task<(string Display, bool Adult, string Label)> ReadStoredGettersAsync()
    {
        await using var db = database!.Store.NewContext();
        var sql = db.GetService<ISqlGenerationHelper>();
        var people = db.Model.FindEntityType(typeof(RelPerson))!;
        var jobs = people.FindNavigation(nameof(RelPerson.Jobs))!.TargetEntityType;
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT p.{sql.DelimitIdentifier("Display")}, p.{sql.DelimitIdentifier("Adult")}, j.{sql.DelimitIdentifier("Label")} " +
            $"FROM {sql.DelimitIdentifier(people.GetTableName()!, people.GetSchema())} p, {sql.DelimitIdentifier(jobs.GetTableName()!, jobs.GetSchema())} j " +
            $"WHERE p.{sql.DelimitIdentifier("Age")} = 41";
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return (reader.GetString(0), Convert.ToBoolean(reader.GetValue(1)), reader.GetString(2));
    }

    /// <summary>
    /// O21: an enum is stored by its name, as RavenDB stores it: in its column, in a list column, and inside a JSON
    /// column. A <c>[Flags]</c> enum stays a number, so a bitwise filter still runs in SQL.
    /// </summary>
    [SkippableFact]
    public async Task An_enum_is_stored_by_its_name_and_a_flags_enum_as_a_number()
    {
        RequireServer();
        var (_, person) = await SeedAsync();
        using (var session = Store.OpenSession())
        {
            var stored = (await session.LoadAsync<RelPerson>(person.Id.Value))!;
            stored.Level = RelLevel.Senior;
            stored.NextLevel = RelLevel.Medior;
            stored.PastLevels = [RelLevel.Junior, RelLevel.Medior];
            stored.Rights = RelRights.Read | RelRights.Write;
            stored.Address!.Level = RelLevel.Medior;
            await session.SaveChangesAsync();
        }

        await using (var db = database!.Store.NewContext())
        {
            var sql = db.GetService<ISqlGenerationHelper>();
            var people = db.Model.FindEntityType(typeof(RelPerson))!;
            var columns = string.Join(", ", new[] { "Level", "NextLevel", "PastLevels", "Rights", "Address" }.Select(sql.DelimitIdentifier));
            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {columns} FROM {sql.DelimitIdentifier(people.GetTableName()!, people.GetSchema())}";
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetValue(0).Should().Be("Senior");
            reader.GetValue(1).Should().Be("Medior");
            var pastLevels = reader.GetValue(2) is string[] array ? string.Join(",", array) : (string)reader.GetValue(2); // PostgreSQL: text[]; SQL Server: JSON
            pastLevels.Should().Contain("Junior").And.Contain("Medior");
            Convert.ToInt32(reader.GetValue(3)).Should().Be(3);
            ((string)reader.GetValue(4)).Should().Contain("\"Medior\"");
        }

        using (var session = Store.OpenSession())
        {
            var loaded = (await session.LoadAsync<RelPerson>(person.Id.Value))!;
            loaded.Level.Should().Be(RelLevel.Senior);
            loaded.NextLevel.Should().Be(RelLevel.Medior);
            loaded.PastLevels.Should().Equal(RelLevel.Junior, RelLevel.Medior);
            loaded.Rights.Should().Be(RelRights.Read | RelRights.Write);
            loaded.Address!.Level.Should().Be(RelLevel.Medior);

            (await session.CountAsync(session.Query<RelPerson>().Where(p => p.Level == RelLevel.Senior))).Should().Be(1);
            (await session.CountAsync(session.Query<RelPerson>().Where(p => p.NextLevel == RelLevel.Medior))).Should().Be(1);
            (await session.CountAsync(session.Query<RelPerson>().Where(p => p.PastLevels.Contains(RelLevel.Medior)))).Should().Be(1);
            (await session.CountAsync(session.Query<RelPerson>().Where(p => (p.Rights & RelRights.Write) != 0))).Should().Be(1);
            (await session.CountAsync(session.Query<RelPerson>().Where(p => p.Level == RelLevel.Junior))).Should().Be(0);
        }
    }

    /// <summary>
    /// A list keeps its order, as a document does: a <c>[ValueObject]</c> list (child table) and a reference list (join
    /// table), stored in an order their keys do not sort in, then reordered (<c>[Sortable]</c> is "order is the array
    /// position").
    /// </summary>
    [SkippableFact]
    public async Task Child_and_reference_lists_keep_their_order()
    {
        RequireServer();
        var (_, person) = await SeedAsync();
        using (var session = Store.OpenSession())
        {
            var stored = (await session.LoadAsync<RelPerson>(person.Id.Value))!;
            stored.Jobs = [new RelJob { Id = "c", Title = "first" }, new RelJob { Id = "a", Title = "second" }, new RelJob { Id = "b", Title = "third" }];
            stored.Skills = [Skill("sql"), Skill("csharp")];
            await session.SaveChangesAsync();
        }

        using (var session = Store.OpenSession())
        {
            var loaded = (await session.LoadAsync<RelPerson>(person.Id.Value))!;
            loaded.Skills.Should().Equal(Skill("sql"), Skill("csharp"));
            loaded.Jobs.Select(j => j.Title).Should().Equal("first", "second", "third");
            loaded.Jobs.Reverse();
            loaded.Skills.Reverse();
            await session.SaveChangesAsync();
        }

        using (var session = Store.OpenSession())
        {
            var reordered = (await session.LoadAsync<RelPerson>(person.Id.Value))!;
            reordered.Jobs.Select(j => j.Title).Should().Equal("third", "second", "first");
            reordered.Skills.Should().Equal(Skill("csharp"), Skill("sql"));
        }
    }

    /// <summary>
    /// PRD §8.10, finding 5: EF joins every AsDetail collection into the owner's read, so two of them in one statement
    /// return the product of their rows (3 jobs × 4 courses = 12 per person). A type with several child tables reads
    /// split: the owner's rows, then one statement per child table. Its lists come back whole and in their stored
    /// order, through a load and through a query.
    /// </summary>
    [SkippableFact]
    public async Task A_type_with_two_child_tables_reads_each_in_a_statement_of_its_own()
    {
        RequireServer();
        var (_, person) = await SeedAsync();
        using (var session = Store.OpenSession())
        {
            var stored = (await session.LoadAsync<RelPerson>(person.Id.Value))!;
            stored.Jobs = [new RelJob { Id = "c", Title = "j1" }, new RelJob { Id = "a", Title = "j2" }, new RelJob { Id = "b", Title = "j3" }];
            stored.Courses = [new RelCourse { Id = "d", Name = "c1" }, new RelCourse { Id = "b", Name = "c2" }, new RelCourse { Id = "c", Name = "c3" }, new RelCourse { Id = "a", Name = "c4" }];
            await session.SaveChangesAsync();
        }
        string[] childTables;
        using (var db = database!.Store.NewContext())
            childTables = [.. db.Model.FindEntityType(typeof(RelPerson))!.GetNavigations().Where(n => n.IsCollection).Select(n => n.TargetEntityType.GetTableName()!)];
        childTables.Should().HaveCount(2);

        lock (commands.Reads)
            commands.Reads.Clear();
        using (var session = Store.OpenSession())
        {
            var loaded = (await session.LoadAsync<RelPerson>(person.Id.Value))!;
            loaded.Jobs.Select(j => j.Title).Should().Equal("j1", "j2", "j3");
            loaded.Courses.Select(c => c.Name).Should().Equal("c1", "c2", "c3", "c4");
        }
        using (var session = Store.OpenSession())
        {
            var queried = await session.ToListAsync(session.Query<RelPerson>().Where(p => p.Name == "Ann"));
            queried.Should().ContainSingle();
            queried[0].Jobs.Select(j => j.Title).Should().Equal("j1", "j2", "j3");
            queried[0].Courses.Select(c => c.Name).Should().Equal("c1", "c2", "c3", "c4");
        }

        string[] reads;
        lock (commands.Reads)
            reads = [.. commands.Reads];
        reads.Should().NotContain(sql => childTables.All(table => sql.Contains(table)), "each child table is read in a statement of its own");
        foreach (var table in childTables)
            reads.Count(sql => sql.Contains(table)).Should().Be(2, $"the load and the query each read {table} once");
    }

    [SkippableFact]
    public async Task An_upper_case_id_finds_the_row()
    {
        RequireServer();
        var (_, person) = await SeedAsync();

        using var session = Store.OpenSession();
        (await session.LoadAsync<RelPerson>(person.Id.Value.ToUpperInvariant())).Should().NotBeNull();
        (await session.ExistsAsync(typeof(RelPerson), person.Id.Value.ToUpperInvariant())).Should().BeTrue();
        (await session.LoadAsync<RelPerson>("relcompanies/" + Guid.NewGuid().ToString("N"))).Should().BeNull();
    }

    [SkippableFact]
    public async Task The_version_moves_with_the_row_a_child_row_and_a_join_row()
    {
        RequireServer();
        var (_, person) = await SeedAsync();

        await ChangeAsync(person.Id, p => p.Age++);
        (await VersionAsync(person.Id)).Should().Be("2");

        await ChangeAsync(person.Id, p => p.Jobs[0].Title = "Lead");
        (await VersionAsync(person.Id)).Should().Be("3");

        await ChangeAsync(person.Id, p => p.Skills.RemoveAt(0));
        (await VersionAsync(person.Id)).Should().Be("4");

        using var session = Store.OpenSession();
        var loaded = (await session.LoadAsync<RelPerson>(person.Id.Value))!;
        loaded.Jobs[0].Title.Should().Be("Lead");
        loaded.Skills.Should().BeEquivalentTo([Skill("sql")]);
        loaded.Age.Should().Be(42);
    }

    private async Task ChangeAsync(Id<RelPerson> id, Action<RelPerson> change)
    {
        using var session = Store.OpenSession();
        change((await session.LoadAsync<RelPerson>(id.Value))!);
        await session.SaveChangesAsync();
    }

    private async Task<string?> VersionAsync(Id<RelPerson> id)
    {
        using var session = Store.OpenSession();
        return (await session.GetVersionsAsync([id])).GetValueOrDefault(id.Value);
    }

    [SkippableFact]
    public async Task A_stale_version_and_an_insert_over_an_existing_row_fail_as_concurrency()
    {
        RequireServer();
        var (_, person) = await SeedAsync();

        using (var stale = Store.OpenSession())
        {
            var loaded = (await stale.LoadAsync<RelPerson>(person.Id.Value))!;
            loaded.Age = 1;
            await stale.StoreAsync(loaded, "0", person.Id.Value);
            await stale.Invoking(s => s.SaveChangesAsync()).Should().ThrowAsync<SparkStoreConcurrencyException>();
        }

        using var insert = Store.OpenSession();
        await insert.StoreAsync(new RelPerson { Name = "Copy" }, "", person.Id.Value);
        await insert.Invoking(s => s.SaveChangesAsync()).Should().ThrowAsync<SparkStoreConcurrencyException>();
    }

    [SkippableFact]
    public async Task Storing_a_new_instance_over_an_existing_row_overwrites_it()
    {
        RequireServer();
        var (company, person) = await SeedAsync();

        using (var session = Store.OpenSession())
        {
            var replacement = new RelPerson
            {
                Id = person.Id,
                Name = "Ann B.",
                CompanyId = company.Id,
                Skills = [Skill("sql")],
                Jobs = [new RelJob { Id = person.Jobs[0].Id, Title = "Architect" }, new RelJob { Title = "Mentor" }],
            };
            await session.StoreAsync(replacement, "1", person.Id.Value);
            await session.SaveChangesAsync();
            session.GetVersion(replacement).Should().Be("2");
        }

        using var check = Store.OpenSession();
        var loaded = (await check.LoadAsync<RelPerson>(person.Id.Value))!;
        loaded.Name.Should().Be("Ann B.");
        loaded.Address.Should().BeNull();
        loaded.Jobs.Select(j => j.Title).Should().BeEquivalentTo(["Architect", "Mentor"]);
        loaded.Skills.Should().BeEquivalentTo([Skill("sql")]);
    }

    [SkippableFact]
    public async Task A_delete_by_id_removes_the_aggregate_and_a_stale_one_fails()
    {
        RequireServer();
        var (company, person) = await SeedAsync();

        using (var stale = Store.OpenSession())
        {
            stale.Delete(person.Id, "7");
            await stale.Invoking(s => s.SaveChangesAsync()).Should().ThrowAsync<SparkStoreConcurrencyException>();
        }

        using (var session = Store.OpenSession())
        {
            session.Delete(person.Id);
            session.Delete(NewId<RelPerson>("relpeople")); // already gone: nothing to delete
            await session.SaveChangesAsync();
        }

        using var check = Store.OpenSession();
        (await check.ExistsAsync(person.Id)).Should().BeFalse();
        (await check.ExistsAsync(company.Id)).Should().BeTrue();
    }

    [SkippableFact]
    public async Task A_prefix_scan_escapes_like_wildcards()
    {
        RequireServer();
        using (var session = Store.OpenSession())
        {
            await session.StoreAsync(new RelNote { Text = "1" }, "relnotes/a_b/1");
            await session.StoreAsync(new RelNote { Text = "2" }, "relnotes/axb/2");
            await session.StoreAsync(new RelNote { Text = "3" }, "relnotes/a%c/3");
            await session.StoreAsync(new RelSkill { Id = Skill("db_admin"), Label = "x" });
            await session.StoreAsync(new RelSkill { Id = Skill("dbxadmin"), Label = "y" });
            await session.SaveChangesAsync();
        }

        using var check = Store.OpenSession();
        (await check.LoadStartingWithAsync<RelNote>("RelNotes/a_b/")).Select(n => n.Text).Should().BeEquivalentTo(["1"]);
        (await check.LoadStartingWithAsync<RelNote>("relnotes/a%")).Select(n => n.Text).Should().BeEquivalentTo(["3"]);
        (await check.LoadStartingWithAsync<RelSkill>("relskills/db_")).Select(s => s.Label).Should().BeEquivalentTo(["x"]);
        (await check.LoadStartingWithAsync<RelSkill>("relskills/")).Should().HaveCount(2);
    }

    [SkippableFact]
    public async Task A_query_over_a_reference_list_runs_on_the_join_table()
    {
        RequireServer();
        var (_, person) = await SeedAsync();
        using (var session = Store.OpenSession())
        {
            await session.StoreAsync(new RelPerson { Id = NewId<RelPerson>("relpeople"), Name = "Bob", Skills = [Skill("csharp")] });
            await session.SaveChangesAsync();
        }

        using var check = Store.OpenSession();
        var sql = Skill("sql");
        var withSql = await check.ToListAsync(check.Query<RelPerson>().Where(p => p.Skills.Contains(sql)));
        withSql.Select(p => p.Name).Should().BeEquivalentTo(["Ann"]);
        withSql[0].Skills.Should().HaveCount(2);
        (await check.CountAsync(check.Query<RelPerson>().Where(p => p.Skills.Any(s => s == sql) && p.Age > 40))).Should().Be(1);
        _ = person;
    }

    [SkippableFact]
    public async Task A_reference_to_a_missing_row_is_a_validation_error_naming_the_attribute()
    {
        RequireServer();
        using var session = Store.OpenSession();
        await session.StoreAsync(new RelPerson { Id = NewId<RelPerson>("relpeople"), Name = "Dangling", CompanyId = NewId<RelCompany>("relcompanies") });

        var error = await session.Invoking(s => s.SaveChangesAsync()).Should().ThrowAsync<SparkValidationException>();
        error.Which.AttributeName.Should().Be(nameof(RelPerson.CompanyId));
    }
}

public sealed class SqlServerStoreTests : RelationalStoreTests
{
    protected override RelationalTestServer? Server => RelationalTestServer.SqlServer;
}

public sealed class PostgreSqlStoreTests : RelationalStoreTests
{
    protected override RelationalTestServer? Server => RelationalTestServer.PostgreSql;
}
