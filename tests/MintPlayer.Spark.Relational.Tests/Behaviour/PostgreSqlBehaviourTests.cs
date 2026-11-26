using Npgsql;

namespace MintPlayer.Spark.Relational.Tests.Behaviour;

/// <summary>
/// PRD D31: what PostgreSQL does that a decision relies on, one test per assumption, named after the decision. They test
/// the server with plain SQL, so a new version or setting fails a named test instead of corrupting data quietly.
/// </summary>
public sealed class PostgreSqlBehaviourTests : PostgreSqlBehaviourDatabase
{
    private const string StampTrigger = """
        CREATE FUNCTION set_stamp() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN NEW.stamp := pg_current_xact_id(); RETURN NEW; END $$;
        CREATE TABLE t (id int GENERATED ALWAYS AS IDENTITY PRIMARY KEY, name text, stamp xid8 NOT NULL DEFAULT '0');
        CREATE TRIGGER t_stamp BEFORE INSERT OR UPDATE ON t FOR EACH ROW EXECUTE FUNCTION set_stamp();
        """;

    [SkippableFact]
    public async Task D6_the_stamp_trigger_moves_the_stamp_on_every_update()
    {
        await using var db = await OpenAsync();
        await ExecuteAsync(db, StampTrigger);
        await ExecuteAsync(db, "INSERT INTO t (name) VALUES ('a')");
        var first = await ScalarAsync<string>(db, "SELECT stamp::text FROM t");
        await ExecuteAsync(db, "UPDATE t SET name = 'b'");
        ulong.Parse(await ScalarAsync<string>(db, "SELECT stamp::text FROM t")).Should().BeGreaterThan(ulong.Parse(first));
    }

    /// <summary>
    /// S14: the later writer commits first with the higher stamp, so a max-stamp cursor would skip the slow one; the
    /// horizon <c>pg_snapshot_xmin</c> stays at or below the open transaction's id until it ends.
    /// </summary>
    [SkippableFact]
    public async Task D6_the_horizon_stays_at_or_below_an_open_transactions_stamp()
    {
        await using (var setup = await OpenAsync())
            await ExecuteAsync(setup, StampTrigger);

        await using var slow = await OpenAsync();
        await using var tx = await slow.BeginTransactionAsync();
        var slowStamp = ulong.Parse(await ScalarAsync<string>(slow, "INSERT INTO t (name) VALUES ('slow') RETURNING stamp::text", tx));

        await using var fast = await OpenAsync();
        var fastStamp = ulong.Parse(await ScalarAsync<string>(fast, "INSERT INTO t (name) VALUES ('fast') RETURNING stamp::text"));
        fastStamp.Should().BeGreaterThan(slowStamp);

        await using var reader = await OpenAsync();
        const string horizon = "SELECT pg_snapshot_xmin(pg_current_snapshot())::text";
        ulong.Parse(await ScalarAsync<string>(reader, horizon)).Should().BeLessThanOrEqualTo(slowStamp);

        await tx.CommitAsync();
        ulong.Parse(await ScalarAsync<string>(reader, horizon)).Should().BeGreaterThan(fastStamp);
    }

    [SkippableFact]
    public async Task D7_the_C_collation_tells_case_apart()
    {
        await using var db = await OpenAsync();
        (await ScalarAsync<bool>(db, "SELECT 'abc' COLLATE \"C\" = 'ABC'")).Should().BeFalse();
        (await ScalarAsync<bool>(db, "SELECT 'abcdef' COLLATE \"C\" LIKE 'abc%'")).Should().BeTrue();
    }

    [SkippableFact]
    public async Task D7_LIKE_with_ESCAPE_takes_wildcards_literally()
    {
        await using var db = await OpenAsync();
        await ExecuteAsync(db, "CREATE TABLE e (id text COLLATE \"C\" PRIMARY KEY)");
        await ExecuteAsync(db, @"INSERT INTO e VALUES ('a_b'), ('axb'), ('a%c'), ('a\d')");
        (await ColumnAsync<string>(db, @"SELECT id FROM e WHERE id LIKE 'a\_%' ESCAPE '\'")).Should().Equal(["a_b"]);
        (await ColumnAsync<string>(db, @"SELECT id FROM e WHERE id LIKE 'a\%%' ESCAPE '\'")).Should().Equal(["a%c"]);
        (await ColumnAsync<string>(db, @"SELECT id FROM e WHERE id LIKE 'a\\%' ESCAPE '\'")).Should().Equal(["a\\d"]);
    }

    [SkippableFact]
    public async Task Search_LIKE_respects_case_and_ILIKE_ignores_it()
    {
        await using var db = await OpenAsync();
        (await ScalarAsync<bool>(db, "SELECT 'Alpha' LIKE '%alp%'")).Should().BeFalse();
        (await ScalarAsync<bool>(db, "SELECT 'Alpha' ILIKE '%alp%'")).Should().BeTrue();
        (await ScalarAsync<bool>(db, "SELECT 'Alpha' = 'ALPHA'")).Should().BeFalse("O20: the default collation respects case");
    }

    /// <summary>
    /// O20: what the dialect's case-insensitive collation relies on, on every supported version (17 and 18). Equality
    /// ignores case under it, a pattern search on such a column fails on 17, and <c>COLLATE "default"</c> with
    /// <c>ILIKE</c>, which <c>SparkQuerySqlGenerator</c> renders, works on both.
    /// </summary>
    [SkippableFact]
    public async Task O20_an_ICU_level2_collation_ignores_case_and_ILIKE_needs_the_default_collation()
    {
        await using var db = await OpenAsync();
        await ExecuteAsync(db, "CREATE COLLATION IF NOT EXISTS beh_ignore_case (provider = icu, locale = 'und-u-ks-level2', deterministic = false)");
        await ExecuteAsync(db, "CREATE TABLE ci (name text COLLATE beh_ignore_case)");
        await ExecuteAsync(db, "INSERT INTO ci VALUES ('Alpha')");

        (await ScalarAsync<long>(db, "SELECT COUNT(*) FROM ci WHERE name = 'ALPHA'")).Should().Be(1);
        (await ScalarAsync<long>(db, "SELECT COUNT(*) FROM ci WHERE name IN ('ALPHA', 'x')")).Should().Be(1);
        (await ScalarAsync<long>(db, "SELECT COUNT(*) FROM ci WHERE (name) COLLATE \"default\" ILIKE 'al%'")).Should().Be(1);
        (await ScalarAsync<long>(db, "SELECT COUNT(*) FROM ci WHERE strpos(lower((name) COLLATE \"default\"), lower('LP')) > 0")).Should().Be(1);
        (await ScalarAsync<long>(db, "SELECT COUNT(DISTINCT name) FROM (VALUES ('Alpha' COLLATE beh_ignore_case), ('ALPHA')) v(name)"))
            .Should().Be(1, "the trap: DISTINCT and GROUP BY fold case, as RavenDB's facets do");
        var refused = await Record.ExceptionAsync(() => ScalarAsync<long>(db, "SELECT COUNT(*) FROM ci WHERE name ILIKE 'al%'"));
        refused.Should().NotBeNull("ILIKE refuses a nondeterministic collation on 17 and 18");
    }

    [SkippableFact]
    public async Task D5_timestamptz_compares_instants()
    {
        await using var db = await OpenAsync();
        (await ScalarAsync<bool>(db, "SELECT '2026-06-01T12:00:00+02:00'::timestamptz = '2026-06-01T10:00:00Z'::timestamptz")).Should().BeTrue();
    }

    [SkippableFact]
    public async Task D3_an_update_naming_a_stale_version_reports_zero_rows()
    {
        await using var db = await OpenAsync();
        await ExecuteAsync(db, "CREATE TABLE v (id int PRIMARY KEY, version bigint NOT NULL, name text)");
        await ExecuteAsync(db, "INSERT INTO v VALUES (1, 1, 'a')");
        (await ExecuteAsync(db, "UPDATE v SET name = 'b', version = 2 WHERE id = 1 AND version = @v", null, ("v", 1L))).Should().Be(1);
        (await ExecuteAsync(db, "UPDATE v SET name = 'c', version = 2 WHERE id = 1 AND version = @v", null, ("v", 1L))).Should().Be(0);
    }

    [SkippableFact]
    public async Task D30_the_constraint_errors_carry_the_SQLSTATEs_the_dialect_maps()
    {
        await using var db = await OpenAsync();
        await ExecuteAsync(db, "CREATE TABLE p (id int PRIMARY KEY, code varchar(5) NOT NULL UNIQUE)");
        await ExecuteAsync(db, "CREATE TABLE c (id int PRIMARY KEY, pid int NOT NULL REFERENCES p (id))");
        await ExecuteAsync(db, "INSERT INTO p VALUES (1, 'a')");

        (await StateAsync(db, "INSERT INTO p VALUES (1, 'b')")).Should().Be(PostgresErrorCodes.UniqueViolation);
        (await StateAsync(db, "INSERT INTO p VALUES (2, 'a')")).Should().Be(PostgresErrorCodes.UniqueViolation);
        (await StateAsync(db, "INSERT INTO c VALUES (1, 99)")).Should().Be(PostgresErrorCodes.ForeignKeyViolation);
        (await StateAsync(db, "INSERT INTO p VALUES (3, NULL)")).Should().Be(PostgresErrorCodes.NotNullViolation);
        (await StateAsync(db, "INSERT INTO p VALUES (4, 'toolong')")).Should().Be(PostgresErrorCodes.StringDataRightTruncation);
    }

    private static async Task<string> StateAsync(System.Data.Common.DbConnection db, string sql)
    {
        try
        {
            await ExecuteAsync(db, sql);
        }
        catch (PostgresException e)
        {
            return e.SqlState;
        }
        throw new InvalidOperationException("The statement succeeded: " + sql);
    }

    [SkippableFact]
    public async Task O11_a_column_added_later_fills_existing_rows_with_its_default_or_NULL()
    {
        await using var db = await OpenAsync();
        await ExecuteAsync(db, "CREATE TABLE a (id int PRIMARY KEY)");
        await ExecuteAsync(db, "INSERT INTO a VALUES (1)");
        await ExecuteAsync(db, "ALTER TABLE a ADD COLUMN count int NOT NULL DEFAULT 0, ADD COLUMN note text NULL");
        (await ScalarAsync<int>(db, "SELECT count FROM a")).Should().Be(0);
        (await ScalarAsync<long>(db, "SELECT COUNT(*) FROM a WHERE note IS NULL")).Should().Be(1);
    }

    [SkippableFact]
    public async Task O12_RETURNING_gives_the_updated_rows_in_the_transaction_and_a_rollback_discards_them()
    {
        await using var db = await OpenAsync();
        await ExecuteAsync(db, "CREATE TABLE r (id int PRIMARY KEY, name text)");
        await ExecuteAsync(db, "CREATE TABLE rev (id int, name text)");
        await ExecuteAsync(db, "INSERT INTO r VALUES (1, 'a'), (2, 'b')");

        await using (var tx = await db.BeginTransactionAsync())
        {
            (await ExecuteAsync(db, "WITH u AS (UPDATE r SET name = 'z' WHERE id = 1 RETURNING id, name) INSERT INTO rev SELECT * FROM u", tx)).Should().Be(1);
            (await ScalarAsync<string>(db, "SELECT name FROM rev WHERE id = 1", tx)).Should().Be("z");
            await tx.RollbackAsync();
        }

        (await ScalarAsync<string>(db, "SELECT name FROM r WHERE id = 1")).Should().Be("a");
        (await ScalarAsync<long>(db, "SELECT COUNT(*) FROM rev")).Should().Be(0);
    }

    /// <summary>S12: jsonb keeps neither key order nor duplicate keys, so a stored document is compared by value, never as text.</summary>
    [SkippableFact]
    public async Task S12_jsonb_reorders_keys_and_keeps_the_last_duplicate()
    {
        await using var db = await OpenAsync();
        (await ScalarAsync<string>(db, """SELECT '{"b":1,"a":2,"b":3}'::jsonb::text""")).Should().Be("""{"a": 2, "b": 3}""");
    }

    [SkippableFact]
    public async Task D32_a_view_returns_a_committed_write_at_once_and_a_row_per_child()
    {
        await using var db = await OpenAsync();
        await ExecuteAsync(db, "CREATE TABLE o (id int PRIMARY KEY, name text)");
        await ExecuteAsync(db, "CREATE TABLE l (id int PRIMARY KEY, oid int REFERENCES o (id), sku text)");
        await ExecuteAsync(db, "CREATE VIEW ol AS SELECT o.id, o.name, l.sku FROM o JOIN l ON l.oid = o.id");
        await ExecuteAsync(db, "INSERT INTO o VALUES (1, 'order')");
        await ExecuteAsync(db, "INSERT INTO l VALUES (1, 1, 'x'), (2, 1, 'y')");

        await using var reader = await OpenAsync();
        (await ColumnAsync<string>(reader, "SELECT sku FROM ol ORDER BY sku")).Should().Equal(["x", "y"]);
    }
}
