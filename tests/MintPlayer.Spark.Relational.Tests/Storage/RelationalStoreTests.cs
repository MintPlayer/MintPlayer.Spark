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
}

public class RelAddress
{
    public string Street { get; set; } = string.Empty;
    public int Number { get; set; }
}

public class RelPerson
{
    public Id<RelPerson> Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
    public Id<RelCompany>? CompanyId { get; set; }
    public List<Id<RelSkill>> Skills { get; set; } = [];
    public List<RelJob> Jobs { get; set; } = [];
    public RelAddress? Address { get; set; }
    public TranslatedString? Title { get; set; }
    public List<string> Tags { get; set; } = [];
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

    protected abstract RelationalTestServer? Server { get; }

    protected ISparkStore Store => database!.Store;

    public async Task InitializeAsync()
    {
        if (Server is { } server)
            database = await RelationalTestDatabase.CreateAsync(server, [typeof(RelCompany), typeof(RelSkill), typeof(RelPerson), typeof(RelNote)]);
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
