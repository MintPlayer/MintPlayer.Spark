using Microsoft.Data.SqlClient;

namespace MintPlayer.Spark.Relational.Tests.Behaviour;

/// <summary>
/// PRD D31: what SQL Server does that a decision relies on, one test per assumption, named after the decision. They test
/// the server with plain SQL, so a new version or setting fails a named test instead of corrupting data quietly.
/// </summary>
public sealed class SqlServerBehaviourTests : SqlServerBehaviourDatabase
{
    [SkippableFact]
    public async Task D6_rowversion_moves_on_every_update_and_DBTS_on_any_commit()
    {
        await using var db = await OpenAsync();
        await ExecuteAsync(db, "CREATE TABLE T (Id int PRIMARY KEY, Name nvarchar(20), Stamp rowversion)");
        await ExecuteAsync(db, "INSERT INTO T (Id, Name) VALUES (1, N'a')");
        var first = await ScalarAsync<long>(db, "SELECT CAST(Stamp AS bigint) FROM T");
        var dbts = await ScalarAsync<long>(db, "SELECT CAST(@@DBTS AS bigint)");

        await ExecuteAsync(db, "UPDATE T SET Name = N'b'");
        (await ScalarAsync<long>(db, "SELECT CAST(Stamp AS bigint) FROM T")).Should().BeGreaterThan(first);
        (await ScalarAsync<long>(db, "SELECT CAST(@@DBTS AS bigint)")).Should().BeGreaterThan(dbts);
    }

    /// <summary>
    /// S14: a cursor that reads "everything up to the newest stamp" skips a row whose transaction commits later; the
    /// horizon <c>MIN_ACTIVE_ROWVERSION()</c> stays at or below the open transaction's stamp, so a cursor bounded by it
    /// waits for that row instead.
    /// </summary>
    [SkippableFact]
    public async Task D6_the_horizon_stays_at_or_below_an_open_transactions_stamp()
    {
        await using (var setup = await OpenAsync())
            await ExecuteAsync(setup, "CREATE TABLE T (Id int IDENTITY PRIMARY KEY, Name nvarchar(20), Stamp rowversion)");

        await using var slow = await OpenAsync();
        await using var tx = await slow.BeginTransactionAsync();
        var slowStamp = await ScalarAsync<long>(slow, "INSERT INTO T (Name) OUTPUT CAST(inserted.Stamp AS bigint) VALUES (N'slow')", tx);

        await using var fast = await OpenAsync();
        var fastStamp = await ScalarAsync<long>(fast, "INSERT INTO T (Name) OUTPUT CAST(inserted.Stamp AS bigint) VALUES (N'fast')");
        fastStamp.Should().BeGreaterThan(slowStamp, "the later writer commits first with the higher stamp: a max-stamp cursor would skip 'slow'");

        await using var reader = await OpenAsync();
        (await ScalarAsync<long>(reader, "SELECT CAST(MIN_ACTIVE_ROWVERSION() AS bigint)")).Should().BeLessThanOrEqualTo(slowStamp);

        await tx.CommitAsync();
        (await ScalarAsync<long>(reader, "SELECT CAST(MIN_ACTIVE_ROWVERSION() AS bigint)")).Should().BeGreaterThan(fastStamp);
    }

    [SkippableFact]
    public async Task D7_the_binary_collation_tells_case_apart()
    {
        await using var db = await OpenAsync();
        (await ScalarAsync<int>(db, "SELECT CASE WHEN N'abc' COLLATE Latin1_General_100_BIN2 = N'ABC' THEN 1 ELSE 0 END")).Should().Be(0);
        (await ScalarAsync<int>(db, "SELECT CASE WHEN N'abcdef' COLLATE Latin1_General_100_BIN2 LIKE N'abc%' THEN 1 ELSE 0 END")).Should().Be(1);
    }

    [SkippableFact]
    public async Task D7_a_foreign_key_across_collations_is_refused()
    {
        await using var db = await OpenAsync();
        await ExecuteAsync(db, "CREATE TABLE P (Id nvarchar(50) COLLATE Latin1_General_100_BIN2 PRIMARY KEY)");
        var act = () => ExecuteAsync(db, "CREATE TABLE C (Id int PRIMARY KEY, PId nvarchar(50) COLLATE Latin1_General_CI_AS REFERENCES P (Id))");
        await act.Should().ThrowAsync<SqlException>();
    }

    [SkippableFact]
    public async Task D7_a_key_over_900_bytes_is_refused()
    {
        await using var db = await OpenAsync();
        await ExecuteAsync(db, "CREATE TABLE K (Id nvarchar(451) COLLATE Latin1_General_100_BIN2 PRIMARY KEY)");
        var act = () => ExecuteAsync(db, "INSERT INTO K (Id) VALUES (@id)", null, ("@id", new string('x', 451)));
        await act.Should().ThrowAsync<SqlException>();
        (await ExecuteAsync(db, "INSERT INTO K (Id) VALUES (@id)", null, ("@id", new string('x', 450)))).Should().Be(1);
    }

    [SkippableFact]
    public async Task D7_LIKE_with_ESCAPE_takes_wildcards_literally()
    {
        await using var db = await OpenAsync();
        await ExecuteAsync(db, "CREATE TABLE E (Id nvarchar(20) COLLATE Latin1_General_100_BIN2 PRIMARY KEY)");
        await ExecuteAsync(db, @"INSERT INTO E VALUES (N'a_b'), (N'axb'), (N'a%c'), (N'a\d'), (N'a[e')");
        (await ColumnAsync<string>(db, @"SELECT Id FROM E WHERE Id LIKE N'a\_%' ESCAPE N'\'")).Should().Equal(["a_b"]);
        (await ColumnAsync<string>(db, @"SELECT Id FROM E WHERE Id LIKE N'a\%%' ESCAPE N'\'")).Should().Equal(["a%c"]);
        (await ColumnAsync<string>(db, @"SELECT Id FROM E WHERE Id LIKE N'a\\%' ESCAPE N'\'")).Should().Equal(["a\\d"]);
        (await ColumnAsync<string>(db, @"SELECT Id FROM E WHERE Id LIKE N'a\[%' ESCAPE N'\'")).Should().Equal(["a[e"]);
    }

    [SkippableFact]
    public async Task Search_LIKE_on_an_ordinary_column_ignores_case()
    {
        await using var db = await OpenAsync();
        await ExecuteAsync(db, "CREATE TABLE S (Id int PRIMARY KEY, Name nvarchar(50))");
        await ExecuteAsync(db, "INSERT INTO S VALUES (1, N'Alpha')");
        (await ScalarAsync<int>(db, "SELECT COUNT(*) FROM S WHERE Name LIKE N'%alp%'")).Should().Be(1);
        (await ScalarAsync<int>(db, "SELECT COUNT(*) FROM S WHERE Name = N'ALPHA'"))
            .Should().Be(1, "O20: equality ignores case too, under the database's (default, case-insensitive) collation");
    }

    [SkippableFact]
    public async Task D5_datetimeoffset_compares_instants()
    {
        await using var db = await OpenAsync();
        (await ScalarAsync<int>(db,
            "SELECT CASE WHEN CAST('2026-06-01T12:00:00+02:00' AS datetimeoffset) = CAST('2026-06-01T10:00:00Z' AS datetimeoffset) THEN 1 ELSE 0 END"))
            .Should().Be(1);
    }

    [SkippableFact]
    public async Task D3_an_update_naming_a_stale_version_reports_zero_rows()
    {
        await using var db = await OpenAsync();
        await ExecuteAsync(db, "CREATE TABLE V (Id int PRIMARY KEY, Version bigint NOT NULL, Name nvarchar(20))");
        await ExecuteAsync(db, "INSERT INTO V VALUES (1, 1, N'a')");
        (await ExecuteAsync(db, "UPDATE V SET Name = N'b', Version = 2 WHERE Id = 1 AND Version = @v", null, ("@v", 1L))).Should().Be(1);
        (await ExecuteAsync(db, "UPDATE V SET Name = N'c', Version = 2 WHERE Id = 1 AND Version = @v", null, ("@v", 1L))).Should().Be(0);
    }

    [SkippableFact]
    public async Task D30_the_constraint_errors_carry_the_numbers_the_dialect_maps()
    {
        await using var db = await OpenAsync();
        await ExecuteAsync(db, "CREATE TABLE P (Id int PRIMARY KEY, Code nvarchar(5) NOT NULL CONSTRAINT UQ_P_Code UNIQUE)");
        await ExecuteAsync(db, "CREATE UNIQUE INDEX IX_P_Code2 ON P (Code, Id)");
        await ExecuteAsync(db, "CREATE TABLE C (Id int PRIMARY KEY, PId int NOT NULL REFERENCES P (Id))");
        await ExecuteAsync(db, "INSERT INTO P VALUES (1, N'a')");

        (await NumberAsync(db, "INSERT INTO P VALUES (1, N'b')")).Should().Be(2627, "a primary key violation");
        (await NumberAsync(db, "INSERT INTO P VALUES (2, N'a')")).Should().Be(2627, "a unique constraint violation");
        (await NumberAsync(db, "INSERT INTO C VALUES (1, 99)")).Should().Be(547, "a foreign key violation");
        (await NumberAsync(db, "INSERT INTO P VALUES (3, NULL)")).Should().Be(515, "a not-null violation");
        (await NumberAsync(db, "INSERT INTO P VALUES (4, N'toolong')")).Should().Be(2628, "a length violation");

        await ExecuteAsync(db, "CREATE TABLE U (Id int PRIMARY KEY, Code nvarchar(5))");
        await ExecuteAsync(db, "CREATE UNIQUE INDEX IX_U_Code ON U (Code)");
        await ExecuteAsync(db, "INSERT INTO U VALUES (1, N'a')");
        (await NumberAsync(db, "INSERT INTO U VALUES (2, N'a')")).Should().Be(2601, "a unique index violation");
    }

    private static async Task<int> NumberAsync(System.Data.Common.DbConnection db, string sql)
    {
        try
        {
            await ExecuteAsync(db, sql);
        }
        catch (SqlException e)
        {
            return e.Number;
        }
        throw new InvalidOperationException("The statement succeeded: " + sql);
    }

    [SkippableFact]
    public async Task O11_a_column_added_later_fills_existing_rows_with_its_default_or_NULL()
    {
        await using var db = await OpenAsync();
        await ExecuteAsync(db, "CREATE TABLE A (Id int PRIMARY KEY)");
        await ExecuteAsync(db, "INSERT INTO A VALUES (1)");
        await ExecuteAsync(db, "ALTER TABLE A ADD Count int NOT NULL CONSTRAINT DF_A_Count DEFAULT 0, Note nvarchar(20) NULL");
        (await ScalarAsync<int>(db, "SELECT Count FROM A")).Should().Be(0);
        (await ScalarAsync<int>(db, "SELECT COUNT(*) FROM A WHERE Note IS NULL")).Should().Be(1);
    }

    [SkippableFact]
    public async Task O12_OUTPUT_returns_the_updated_rows_in_the_transaction_and_a_rollback_discards_both()
    {
        await using var db = await OpenAsync();
        await ExecuteAsync(db, "CREATE TABLE R (Id int PRIMARY KEY, Name nvarchar(20))");
        await ExecuteAsync(db, "CREATE TABLE Rev (Id int, Name nvarchar(20))");
        await ExecuteAsync(db, "INSERT INTO R VALUES (1, N'a'), (2, N'b')");

        await using (var tx = await db.BeginTransactionAsync())
        {
            (await ExecuteAsync(db, "UPDATE R SET Name = N'z' OUTPUT inserted.Id, inserted.Name INTO Rev WHERE Id = 1", tx)).Should().Be(1);
            (await ScalarAsync<string>(db, "SELECT Name FROM Rev WHERE Id = 1", tx)).Should().Be("z");
            await tx.RollbackAsync();
        }

        (await ScalarAsync<string>(db, "SELECT Name FROM R WHERE Id = 1")).Should().Be("a");
        (await ScalarAsync<int>(db, "SELECT COUNT(*) FROM Rev")).Should().Be(0);
    }

    [SkippableFact]
    public async Task D32_a_view_returns_a_committed_write_at_once_and_a_row_per_child()
    {
        await using var db = await OpenAsync();
        await ExecuteAsync(db, "CREATE TABLE O (Id int PRIMARY KEY, Name nvarchar(20))");
        await ExecuteAsync(db, "CREATE TABLE L (Id int PRIMARY KEY, OId int REFERENCES O (Id), Sku nvarchar(20))");
        await ExecuteAsync(db, "CREATE VIEW OL AS SELECT o.Id, o.Name, l.Sku FROM O o JOIN L l ON l.OId = o.Id");
        await ExecuteAsync(db, "INSERT INTO O VALUES (1, N'order')");
        await ExecuteAsync(db, "INSERT INTO L VALUES (1, 1, N'x'), (2, 1, N'y')");

        await using var reader = await OpenAsync();
        (await ColumnAsync<string>(reader, "SELECT Sku FROM OL ORDER BY Sku")).Should().Equal(["x", "y"]);
    }
}
