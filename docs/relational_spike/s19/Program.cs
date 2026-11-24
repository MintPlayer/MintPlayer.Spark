// #536 spike S19: applying migrations (PRD D33). Throwaway.
// dotnet run -- <sqlserver|postgres>     (PostgreSQL password from SPARK536_PG_PASSWORD)
using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

var provider = args.Length > 0 ? args[0] : "sqlserver";
var results = new List<(string Check, bool Ok, string Detail)>();
void Report(string check, bool ok, string detail) { results.Add((check, ok, detail)); Console.WriteLine($"[{(ok ? "OK  " : "FAIL")}] {check}: {detail}"); }

string Cs(string db) => provider == "sqlserver"
    ? $@"Server=localhost\MSSQLSERVER01;Database={db};Trusted_Connection=True;TrustServerCertificate=True"
    : $"Host=127.0.0.1;Port=55432;Username=postgres;Password={Environment.GetEnvironmentVariable("SPARK536_PG_PASSWORD")};Database={db}";
DbContextOptions<T> Opt<T>(string db, string history) where T : DbContext
{
    var b = new DbContextOptionsBuilder<T>();
    if (provider == "sqlserver") b.UseSqlServer(Cs(db), o => o.MigrationsHistoryTable(history));
    else b.UseNpgsql(Cs(db), o => o.MigrationsHistoryTable(history));
    if (Environment.GetEnvironmentVariable("S19_LOG") == "1") b.LogTo(s => { lock (SqlLog.Gate) File.AppendAllText("sql.log", s + Environment.NewLine); }, Microsoft.Extensions.Logging.LogLevel.Debug);
    return b.Options;
}
FrameworkDb Fw(string db) => new(Opt<FrameworkDb>(db, "__SparkFrameworkMigrations"));
AppDb App(string db, bool failing = false) => new(Opt<AppDb>(db, "__EFMigrationsHistory"), failing);

async Task Fresh(string db) { await using var c = Fw(db); await c.Database.EnsureDeletedAsync(); }
async Task<long> Scalar(DbContext c, string sql)
{
    var conn = c.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
    await using var cmd = conn.CreateCommand(); cmd.CommandText = sql;
    return Convert.ToInt64(await cmd.ExecuteScalarAsync());
}
string Q(string id) => provider == "sqlserver" ? $"[{id}]" : $"\"{id}\"";
string TableExists(string t) => provider == "sqlserver" ? $"SELECT COUNT(*) FROM sys.tables WHERE name = '{t}'" : $"SELECT COUNT(*) FROM information_schema.tables WHERE table_name = '{t}'";

// The startup sequence D33 prescribes: framework schema -> app migrations -> data migrations.
async Task<string> Startup(string db, string who, bool failing = false)
{
    var log = new List<string>();
    var sw = Stopwatch.StartNew();
    await using (var fw = Fw(db)) { await fw.Database.MigrateAsync(); log.Add($"fw@{sw.ElapsedMilliseconds}"); }
    FailSwitch.Value = failing;
    try { await using var app = App(db, failing); await app.Database.MigrateAsync(); log.Add($"app@{sw.ElapsedMilliseconds}"); }
    finally { FailSwitch.Value = false; }
    await using (var app = App(db))
    {
        // Data migration stand-in: idempotent by its own record, as ISparkMigration is.
        var done = await Scalar(app, $"SELECT COUNT(*) FROM {Q("SparkDataMigrations")} WHERE {Q("Name")} = 'seed'");
        if (done == 0)
        {
            await app.Database.ExecuteSqlRawAsync($"INSERT INTO {Q("People")} ({Q("Name")}) VALUES ('seeded by {who}'); INSERT INTO {Q("SparkDataMigrations")} ({Q("Name")}) VALUES ('seed');");
        }
        log.Add($"data@{sw.ElapsedMilliseconds}");
    }
    return $"{who}: {string.Join(" ", log)}";
}

// 1. Two instances start together. The second app migration sleeps 3 s, so the two overlap inside the lock.
{
    var db = "sparkspike536mig1";
    await Fresh(db);
    var sw = Stopwatch.StartNew();
    var t1 = Startup(db, "A");
    var t2 = Startup(db, "B");
    string[] outs = [];
    try { outs = await Task.WhenAll(t1, t2); Report("two instances: both start without an error", true, $"{string.Join("; ", outs)} ({sw.ElapsedMilliseconds} ms)"); }
    catch (Exception ex) { Report("two instances: both start without an error", false, Flatten(ex)); }
    await using var c = App(db);
    var hist = await Scalar(c, $"SELECT COUNT(*) FROM {Q("__EFMigrationsHistory")}");
    var fwHist = await Scalar(c, $"SELECT COUNT(*) FROM {Q("__SparkFrameworkMigrations")}");
    var seeded = await Scalar(c, $"SELECT COUNT(*) FROM {Q("People")}");
    Report("two instances: each migration applied once", hist == 3 && fwHist == 1, $"app history {hist}, framework history {fwHist}");
    Report("two instances: the data migration ran once", seeded == 1, $"{seeded} seeded rows (the data step has no lock in this spike)");
}

// 1b. The same, but the (empty) database exists before the instances start.
{
    var db = "sparkspike536mig1b";
    await Fresh(db);
    await using (var fw = Fw(db)) await fw.Database.EnsureCreatedAsync(); // empty model: creates only the database
    var sw = Stopwatch.StartNew();
    try
    {
        var outs = await Task.WhenAll(Startup(db, "A"), Startup(db, "B"), Startup(db, "C"));
        Report("1b three instances on an existing database: no error", true, $"{string.Join("; ", outs)} ({sw.ElapsedMilliseconds} ms)");
    }
    catch (Exception ex) { Report("1b three instances on an existing database: no error", false, Flatten(ex)); }
    await using var c = App(db);
    var hist = await Scalar(c, $"SELECT COUNT(*) FROM {Q("__EFMigrationsHistory")}");
    var fwHist = await Scalar(c, $"SELECT COUNT(*) FROM {Q("__SparkFrameworkMigrations")}");
    var seeded = await Scalar(c, $"SELECT COUNT(*) FROM {Q("People")}");
    Report("1b each migration applied once", hist == 3 && fwHist == 1, $"app history {hist}, framework history {fwHist}");
    Report("1b the unlocked data step ran once", seeded == 1, $"{seeded} seeded rows (no lease around the data step here)");
}

// 1c. Spark's own guard: create the database tolerating "already exists", then hold a session-level lock
//     (pg_advisory_lock / sp_getapplock @LockOwner='Session') around framework -> app -> data.
async Task<string> GuardedStartup(string db, string who)
{
    // The guard lives in the server's maintenance database, so it exists before the target database does
    // and also covers its creation (SQL Server: a fresh database can answer "already exists" before it is online).
    await using var lockCtx = Fw(provider == "sqlserver" ? "master" : "postgres");
    var conn = lockCtx.Database.GetDbConnection();
    await conn.OpenAsync();
    var resource = "spark:migrations:" + db;
    void Param(DbCommand c) { var p = c.CreateParameter(); p.ParameterName = provider == "sqlserver" ? "@res" : "res"; p.Value = resource; c.Parameters.Add(p); }
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = provider == "sqlserver"
            ? "DECLARE @r int; EXEC @r = sp_getapplock @Resource = @res, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 600000; SELECT @r;"
            : "SELECT pg_advisory_lock(hashtext(@res))";
        Param(cmd);
        cmd.CommandTimeout = 600;
        var r = await cmd.ExecuteScalarAsync();
        if (provider == "sqlserver" && Convert.ToInt32(r) < 0) throw new InvalidOperationException($"sp_getapplock returned {r}");
    }
    try
    {
        await using (var fw = Fw(db)) await fw.Database.EnsureCreatedAsync(); // inside the guard: no creation race
        return await Startup(db, who);
    }
    finally
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = provider == "sqlserver"
            ? "EXEC sp_releaseapplock @Resource = @res, @LockOwner = 'Session';"
            : "SELECT pg_advisory_unlock(hashtext(@res))";
        Param(cmd);
        await cmd.ExecuteScalarAsync();
    }
}
{
    var db = "sparkspike536mig1c";
    await Fresh(db);
    var sw = Stopwatch.StartNew();
    try
    {
        var outs = await Task.WhenAll(GuardedStartup(db, "A"), GuardedStartup(db, "B"), GuardedStartup(db, "C"));
        Report("1c three guarded instances on a database that does not exist: no error", true, $"{string.Join("; ", outs)} ({sw.ElapsedMilliseconds} ms)");
    }
    catch (Exception ex) { Report("1c three guarded instances on a database that does not exist: no error", false, Flatten(ex)); }
    await using var c = App(db);
    var hist = await Scalar(c, $"SELECT COUNT(*) FROM {Q("__EFMigrationsHistory")}");
    var fwHist = await Scalar(c, $"SELECT COUNT(*) FROM {Q("__SparkFrameworkMigrations")}");
    var seeded = await Scalar(c, $"SELECT COUNT(*) FROM {Q("People")}");
    Report("1c each migration applied once, the data step once", hist == 3 && fwHist == 1 && seeded == 1, $"app history {hist}, framework history {fwHist}, seeded {seeded}");
}

// 2. A failure halfway: app migration 3 of 3 fails. What did 1 and 2 leave?
{
    var db = "sparkspike536mig2";
    await Fresh(db);
    try { await Startup(db, "F", failing: true); Report("failing migration throws", false, "no exception"); }
    catch (Exception ex) { Report("failing migration throws", true, Flatten(ex)); }
    await using var c = App(db);
    var hist = await Scalar(c, $"SELECT COUNT(*) FROM {Q("__EFMigrationsHistory")}");
    var people = await Scalar(c, TableExists("People"));
    var halfTable = await Scalar(c, TableExists("Half"));
    var dataTable = await Scalar(c, TableExists("SparkDataMigrations"));
    Report("failure: the migrations before it stay applied", hist == 2 && people == 1 && dataTable == 1, $"history {hist}, People {people}, SparkDataMigrations {dataTable}");
    Report("failure: the failing migration rolled back as a whole", halfTable == 0, $"table created before the failing statement: {(halfTable == 0 ? "rolled back" : "LEFT BEHIND")}");
    await using var ok = App(db);
    var pending = (await ok.Database.GetPendingMigrationsAsync()).ToList();
    Report("failure: the fixed migration is still pending", pending.Count == 1, string.Join(", ", pending));
    await ok.Database.MigrateAsync();
    Report("failure: a restart with the fix completes", (await Scalar(ok, $"SELECT COUNT(*) FROM {Q("__EFMigrationsHistory")}")) == 3, "3 applied");
}

// 3. One idempotent script: framework + app, run twice on an empty database.
{
    var db = "sparkspike536mig3";
    await Fresh(db);
    string script;
    await using (var fw = Fw(db)) script = fw.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
    await using (var app = App(db)) script += Environment.NewLine + app.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
    Report("script generated without a database", true, $"{script.Length} chars, {script.Split('\n').Length} lines");
    await using (var fw = Fw(db)) await fw.Database.EnsureCreatedAsync(); // an empty database (the empty model creates no table)
    for (var run = 1; run <= 2; run++)
    {
        try
        {
            await using var c = App(db);
            var conn = c.Database.GetDbConnection(); await conn.OpenAsync();
            var batches = provider == "sqlserver"
                ? System.Text.RegularExpressions.Regex.Split(script, @"^\s*GO\s*$", System.Text.RegularExpressions.RegexOptions.Multiline)
                : [script];
            foreach (var batch in batches.Where(b => !string.IsNullOrWhiteSpace(b)))
            {
                await using var cmd = conn.CreateCommand(); cmd.CommandText = batch; await cmd.ExecuteNonQueryAsync();
            }
            var hist = await Scalar(c, $"SELECT COUNT(*) FROM {Q("__EFMigrationsHistory")}");
            var fwHist = await Scalar(c, $"SELECT COUNT(*) FROM {Q("__SparkFrameworkMigrations")}");
            Report($"script run {run}", hist == 3 && fwHist == 1, $"app history {hist}, framework history {fwHist}");
        }
        catch (Exception ex) { Report($"script run {run}", false, Flatten(ex)); }
    }
    await using (var app = App(db))
    {
        var pending = (await app.Database.GetPendingMigrationsAsync()).Count();
        Report("after the script, nothing is pending for MigrateAsync", pending == 0, $"{pending} pending");
    }
    var hasTxn = provider == "sqlserver" ? script.Contains("BEGIN TRANSACTION") : script.Contains("START TRANSACTION");
    Report("script shape", true, $"transaction statements: {hasTxn}; history guards: {System.Text.RegularExpressions.Regex.Matches(script, "MigrationId").Count}");
}

// 4. ApplyMigrations=false: the refusal check.
{
    var db = "sparkspike536mig4";
    await Fresh(db);
    await using var app = App(db);
    var sw = Stopwatch.StartNew();
    var pending = (await app.Database.GetPendingMigrationsAsync()).ToList();
    Report("pending check on a database that does not exist yet", pending.Count == 3, $"{pending.Count} pending in {sw.ElapsedMilliseconds} ms: refuse to start");
    await app.Database.MigrateAsync();
    pending = (await app.Database.GetPendingMigrationsAsync()).ToList();
    Report("pending check after migrating", pending.Count == 0, "start");
}

Console.WriteLine();
Console.WriteLine($"SUMMARY [{provider}] {results.Count(r => r.Ok)}/{results.Count} OK");
foreach (var r in results.Where(r => !r.Ok)) Console.WriteLine($"  FAIL {r.Check}");
return 0;

static string Flatten(Exception ex) { var m = ex.GetType().Name + ": " + ex.Message; for (var i = ex.InnerException; i is not null; i = i.InnerException) m += " | " + i.Message; m = System.Text.RegularExpressions.Regex.Replace(m, @"\s+", " "); return m.Length > 350 ? m[..350] + "…" : m; }

public sealed class FrameworkDb(DbContextOptions<FrameworkDb> o) : DbContext(o);
public sealed class AppDb(DbContextOptions<AppDb> o, bool failing) : DbContext(o)
{
    public bool Failing => failing;
    protected override void OnConfiguring(DbContextOptionsBuilder b) => b.ReplaceService<IModelCacheKeyFactory, FailingKey>();
}
public sealed class FailingKey : IModelCacheKeyFactory { public object Create(DbContext c, bool d) => (c.GetType(), (c as AppDb)?.Failing, d); }

static class Sql
{
    public static bool Pg(MigrationBuilder b) => b.ActiveProvider?.Contains("Npgsql") == true;
}

[DbContext(typeof(FrameworkDb)), Migration("20261124000001_SparkFramework")]
public sealed class SparkFramework : Migration
{
    protected override void Up(MigrationBuilder b) => b.CreateTable("SparkTombstones", t => new { Id = t.Column<string>(maxLength: 450), Stamp = t.Column<long>() }, constraints: t => t.PrimaryKey("PK_SparkTombstones", x => x.Id));
}

[DbContext(typeof(AppDb)), Migration("20261124000101_People")]
public sealed class People : Migration
{
    protected override void Up(MigrationBuilder b)
    {
        b.CreateTable("People", t => new { Id = t.Column<int>().Annotation("SqlServer:Identity", "1, 1").Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn), Name = t.Column<string>(maxLength: 200) }, constraints: t => t.PrimaryKey("PK_People", x => x.Id));
        b.CreateTable("SparkDataMigrations", t => new { Name = t.Column<string>(maxLength: 200) }, constraints: t => t.PrimaryKey("PK_SparkDataMigrations", x => x.Name));
    }
}

[DbContext(typeof(AppDb)), Migration("20261124000102_Slow")]
public sealed class Slow : Migration
{
    protected override void Up(MigrationBuilder b)
    {
        b.Sql(Sql.Pg(b) ? "DO $$BEGIN PERFORM pg_sleep(3); END$$;" : "WAITFOR DELAY '00:00:03';");
        b.AddColumn<string>("Email", "People", maxLength: 200, nullable: true);
    }
}

[DbContext(typeof(AppDb)), Migration("20261124000103_Half")]
public sealed class Half : Migration
{
    protected override void Up(MigrationBuilder b)
    {
        b.CreateTable("Half", t => new { Id = t.Column<int>() }, constraints: t => t.PrimaryKey("PK_Half", x => x.Id));
        // The failing variant: a statement that errors after the table was created in the same migration.
        if (FailSwitch.Value) b.Sql("SELECT * FROM NoSuchTable;");
    }
}
public static class FailSwitch { public static bool Value; }

public static class SqlLog { public static readonly object Gate = new(); }
