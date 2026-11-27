// #536 spike S4: versions (D3), the etag through Spark's own save/delete path (D14), natural and lowercase ids with
// escaped prefixes (D7), and constraint errors as validation messages (D30). Same host and prototype as S2b.
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Storage;

namespace S2b;

/// <summary>A natural-key entity: ids are chosen by the app (<c>tags/a_b</c>), Name is unique and at most 20 long.</summary>
public class Tag { public string? Id { get; set; } public string Name { get; set; } = ""; }

public abstract partial class S2bTests
{
    private static readonly Guid TagTypeId = Guid.Parse("53620000-0000-4000-8000-000000000005");

    private async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> work)
    {
        using var scope = factory.CreateScope();
        return await work(scope.ServiceProvider);
    }

    private async Task<long> StoredVersionAsync(string id)
    {
        using var s = efStore.OpenSession();
        return long.Parse((await s.GetVersionsAsync([id]))[id]);
    }

    /// <summary>Another writer, in its own session: the row moves to the next version.</summary>
    private async Task TouchAsync(string id)
    {
        using var s = efStore.OpenSession();
        var e = (await s.LoadAsync<Employee>(id))!;
        e.Owner = e.Owner; // no change: must not bump
        e.LastName += "!";
        await s.SaveChangesAsync();
    }

    // ---- D3 in the session ----

    [Fact]
    public async Task Versions_start_at_1_and_move_once_per_changed_aggregate()
    {
        (await StoredVersionAsync("employees/ada")).Should().Be(1);

        using (var s = efStore.OpenSession())
        {
            var ada = (await s.LoadAsync<Employee>("employees/ada"))!;
            await s.SaveChangesAsync(); // nothing changed
        }
        (await StoredVersionAsync("employees/ada")).Should().Be(1);

        using (var s = efStore.OpenSession())
        {
            var ada = (await s.LoadAsync<Employee>("employees/ada"))!;
            ada.Addresses[0].City = "Paris"; // a child row only (D25)
            await s.SaveChangesAsync();
        }
        (await StoredVersionAsync("employees/ada")).Should().Be(2);

        using (var s = efStore.OpenSession())
        {
            var ada = (await s.LoadAsync<Employee>("employees/ada"))!;
            ada.Skills = ["skills/sql"]; // a join row only (D26)
            await s.SaveChangesAsync();
        }
        (await StoredVersionAsync("employees/ada")).Should().Be(3);
    }

    [Fact]
    public async Task A_stale_version_fails_the_commit_after_any_earlier_check()
    {
        using var s = efStore.OpenSession();
        var grace = (await s.LoadAsync<Employee>("employees/grace"))!;
        var seen = s.GetVersion(grace);
        await TouchAsync("employees/grace"); // lands after the load, as after DatabaseAccess's side-session check

        grace.FirstName = "G";
        await s.StoreAsync(grace, seen, "employees/grace");
        SqlLog.Clear();
        var act = () => s.SaveChangesAsync();

        await act.Should().ThrowAsync<SparkStoreConcurrencyException>();
        SqlLog.All().Should().Contain("Version");
    }

    [Fact]
    public async Task Empty_expected_version_inserts_only()
    {
        using (var s = efStore.OpenSession())
        {
            await s.StoreAsync(new Tag { Name = "new" }, "", "tags/new");
            await s.SaveChangesAsync();
        }
        (await StoredVersionAsync("tags/new")).Should().Be(1);

        using var again = efStore.OpenSession();
        await again.StoreAsync(new Tag { Name = "other" }, "", "tags/new");
        var act = () => again.SaveChangesAsync();
        await act.Should().ThrowAsync<SparkStoreConcurrencyException>();
    }

    [Fact]
    public async Task Delete_by_id_checks_the_version_and_cascades()
    {
        using (var stale = efStore.OpenSession())
        {
            stale.Delete("employees/ada", "7");
            var act = () => stale.SaveChangesAsync();
            await act.Should().ThrowAsync<SparkStoreConcurrencyException>();
        }

        using (var s = efStore.OpenSession())
        {
            s.Delete("employees/ada", "1");
            await s.SaveChangesAsync();
        }
        using var check = efStore.OpenSession();
        (await check.ExistsAsync("employees/ada")).Should().BeFalse();
        (await check.LoadAsync<Employee>("employees/grace")).Should().NotBeNull();
    }

    // ---- D14: the etag through Spark's own save and delete ----

    [Fact]
    public async Task Save_with_the_loaded_etag_returns_the_next_one()
    {
        var saved = await InScopeAsync(async sp =>
        {
            var access = sp.GetRequiredService<IDatabaseAccess>();
            var po = (await access.GetPersistentObjectAsync(EmployeeTypeId, "employees/grace"))!;
            po.Etag.Should().Be("1");
            po["FirstName"].Value = "Grace B.";
            return await access.SavePersistentObjectAsync(po);
        });

        saved.Etag.Should().Be("2");
        (await StoredVersionAsync("employees/grace")).Should().Be(2);
    }

    [Fact]
    public async Task Save_with_a_stale_etag_is_a_conflict()
    {
        var po = await InScopeAsync(sp => sp.GetRequiredService<IDatabaseAccess>().GetPersistentObjectAsync(EmployeeTypeId, "employees/grace"));
        await TouchAsync("employees/grace");

        po!["FirstName"].Value = "Grace B.";
        var act = () => InScopeAsync(sp => sp.GetRequiredService<IDatabaseAccess>().SavePersistentObjectAsync(po));

        (await act.Should().ThrowAsync<Exception>()).Which.GetType().Name.Should().Be("SparkConcurrencyException");
        (await StoredVersionAsync("employees/grace")).Should().Be(2);
    }

    [Fact]
    public async Task Delete_with_a_stale_etag_is_a_conflict_and_with_the_current_one_deletes()
    {
        await TouchAsync("employees/grace");
        var stale = () => InScopeAsync<bool>(async sp =>
        {
            await sp.GetRequiredService<IDatabaseAccess>().DeletePersistentObjectAsync(EmployeeTypeId, "employees/grace",
                MintPlayer.Spark.Abstractions.Interceptors.PersistentObjectOperation.Delete, etag: "1");
            return true;
        });
        (await stale.Should().ThrowAsync<Exception>()).Which.GetType().Name.Should().Be("SparkConcurrencyException");

        await InScopeAsync<bool>(async sp =>
        {
            await sp.GetRequiredService<IDatabaseAccess>().DeletePersistentObjectAsync(EmployeeTypeId, "employees/grace",
                MintPlayer.Spark.Abstractions.Interceptors.PersistentObjectOperation.Delete, etag: "2");
            return true;
        });
        using var check = efStore.OpenSession();
        (await check.ExistsAsync("employees/grace")).Should().BeFalse();
    }

    [Fact]
    public async Task Query_rows_carry_their_etags()
    {
        await TouchAsync("employees/grace");
        var result = await executor.ExecuteQueryAsync(Query("Employees"));

        // QueryResultProjector reads them through GetVersionsAsync, without loading the rows.
        result.Items.Should().HaveCount(2);
        SqlLog.All().Should().Contain("Version");
    }

    // ---- D7: natural, lowercase ids; an escaped prefix ----

    [Fact]
    public async Task Prefix_scans_treat_underscore_and_percent_as_text()
    {
        using (var s = efStore.OpenSession())
        {
            foreach (var id in new[] { "tags/a_b", "tags/a_bc", "tags/axb", "tags/a%b", "tags/a%%", "tags/azzz" })
                await s.StoreAsync(new Tag { Name = id[5..] }, "", id);
            await s.SaveChangesAsync();
        }

        using var q = efStore.OpenSession();
        (await q.LoadStartingWithAsync<Tag>("tags/a_")).Select(t => t.Id).Should().BeEquivalentTo(["tags/a_b", "tags/a_bc"]);
        (await q.LoadStartingWithAsync<Tag>("tags/a%")).Select(t => t.Id).Should().BeEquivalentTo(["tags/a%b", "tags/a%%"]);
        (await q.LoadStartingWithAsync<Tag>("tags/a%%")).Select(t => t.Id).Should().BeEquivalentTo(["tags/a%%"]);
    }

    [Fact]
    public async Task Key_columns_compare_bytes_so_only_the_canonical_id_is_found()
    {
        using var s = efStore.OpenSession();
        (await s.LoadAsync<Employee>("employees/ada")).Should().NotBeNull();
        // Upper case is lowered at the canonicalisation point (Id<T>, the route binder), never by the database.
        (await s.LoadAsync<Employee>("Employees/Ada")).Should().BeNull();
    }

    // ---- D30: constraint errors become validation messages ----

    private async Task<SparkValidationException> InvalidTagAsync(Tag tag, string id)
    {
        using var s = efStore.OpenSession();
        await s.StoreAsync(tag, "", id);
        var act = () => s.SaveChangesAsync();
        return (await act.Should().ThrowAsync<SparkValidationException>()).Which;
    }

    [Fact]
    public async Task Unique_violation_names_the_attribute()
    {
        using (var s = efStore.OpenSession())
        {
            await s.StoreAsync(new Tag { Name = "dup" }, "", "tags/one");
            await s.SaveChangesAsync();
        }
        var error = await InvalidTagAsync(new Tag { Name = "dup" }, "tags/two");
        error.AttributeName.Should().Be("Name");
        error.Message.Should().Be("Name must be unique.");
    }

    [Fact]
    public async Task Constraint_errors_are_named_under_a_localized_session_language()
    {
        if (provider != "sqlserver") return; // PostgreSQL reports names in fields, never in its message text

        var german = new DbContextOptionsBuilder()
            .UseSqlServer($@"Server=localhost\MSSQLSERVER01;Database=sparkspike536s2b;Trusted_Connection=True;TrustServerCertificate=True;Current Language=Deutsch")
            .Log().Options;
        var store = new EfSparkStore(german, SparkModel(), provider);
        using (var s = store.OpenSession())
        {
            await s.StoreAsync(new Tag { Name = "dup" }, "", "tags/one");
            await s.SaveChangesAsync();
        }

        async Task<SparkValidationException> FailAsync(Tag tag, string id)
        {
            using var s = store.OpenSession();
            await s.StoreAsync(tag, "", id);
            var act = () => s.SaveChangesAsync();
            return (await act.Should().ThrowAsync<SparkValidationException>()).Which;
        }

        (await FailAsync(new Tag { Name = "dup" }, "tags/two")).AttributeName.Should().Be("Name");
        (await FailAsync(new Tag { Name = null! }, "tags/three")).AttributeName.Should().Be("Name");
        using var fk = store.OpenSession();
        await fk.StoreAsync(new Employee { FirstName = "X", LastName = "Y", Owner = "alice", Company = "companies/missing" }, "", "employees/x");
        var act = () => fk.SaveChangesAsync();
        (await act.Should().ThrowAsync<SparkValidationException>()).Which.AttributeName.Should().Be("Company");

        // The localized text is what was parsed (evidence for the PRD).
        File.AppendAllText("sql-sqlserver-german.log", string.Join("\n", ConstraintErrors.Seen) + "\n");
    }

    [Fact]
    public async Task Length_violation_names_the_attribute_and_not_the_value()
    {
        var error = await InvalidTagAsync(new Tag { Name = "secret-value-that-is-too-long" }, "tags/long");
        error.AttributeName.Should().Be("Name");
        error.Message.Should().NotContain("secret");
    }

    [Fact]
    public async Task Not_null_violation_names_the_attribute()
    {
        var error = await InvalidTagAsync(new Tag { Name = null! }, "tags/null");
        error.AttributeName.Should().Be("Name");
    }

    [Fact]
    public async Task Foreign_key_violation_names_the_reference()
    {
        using var s = efStore.OpenSession();
        await s.StoreAsync(new Employee { FirstName = "X", LastName = "Y", Owner = "alice", Company = "companies/missing" }, "", "employees/x");
        var act = () => s.SaveChangesAsync();

        var error = (await act.Should().ThrowAsync<SparkValidationException>()).Which;
        error.AttributeName.Should().Be("Company");
    }
}
