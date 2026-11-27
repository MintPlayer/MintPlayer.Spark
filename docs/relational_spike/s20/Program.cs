// #536 spike S20: Spark commands as keyed DI registrations (PRD D34). Throwaway.
// Runs itself once per scenario in-process: each scenario builds a fresh WebApplication with the given args.
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

var results = new List<(string Check, bool Ok, string Detail)>();
void Report(string check, bool ok, string detail) { results.Add((check, ok, detail)); Console.WriteLine($"[{(ok ? "OK  " : "FAIL")}] {check}: {detail}"); }

async Task<(bool Handled, Snap C, string Out, TimeSpan Took)> Host(string[] args, bool providerOverridesScript = true, string env = "Development")
{
    Counters.Reset();
    var sw = Stopwatch.StartNew();
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, EnvironmentName = env });
    builder.Logging.ClearProviders();
    // Core (AddSpark): the switches and their default implementations.
    builder.Services.AddSparkCore();
    // A provider (AddSparkSqlServer / AddSparkRavenDb): implementations and hook-ins only.
    builder.Services.AddSingleton<FakeDocumentStore>(_ => new FakeDocumentStore());
    builder.Services.AddDbContext<AppDb>(o => o.UseSqlServer("Server=tcp:10.255.255.1,1433;Database=x;Integrated Security=True;Connect Timeout=5;TrustServerCertificate=True"));
    if (providerOverridesScript) builder.Services.AddKeyedScoped<ISparkCommand, SqlMigrationScriptCommand>("--spark-migration-script");
    var app = builder.Build();
    var sb = new StringWriter();
    var handled = await app.RunSparkCommandIfRequestedAsync(args, sb);
    sw.Stop();
    return (handled, Counters.Snapshot(), sb.ToString().Trim(), sw.Elapsed);
}

// 1. Only the requested command is constructed; nothing else.
var r = await Host(["--spark-migrate"]);
Report("--spark-migrate handled", r.Handled && r.Out == "migrate ran", r.Out);
Report("only the requested command constructed", r.C.Migrate == 1 && r.C.Synchronize == 0 && r.C.Script == 0 && r.C.Help == 0, r.C.ToString());

// 2. Synchronize: builds the EF model and diffs it, constructs no store, opens no connection (the server is unreachable).
r = await Host(["--spark-synchronize-model"]);
Report("--spark-synchronize-model handled", r.Handled, r.Out);
Report("synchronize: no document store, no connection", r.C.Store == 0 && r.C.Connections == 0 && r.Out.Contains("operations"), $"{r.C}; {r.Took.TotalSeconds:F1} s");

// 3. Not a command: the app goes on to run.
r = await Host(["--urls", "http://localhost:5999"]);
Report("no command flag: not handled, nothing constructed", !r.Handled && r.C.Total == 0, r.C.ToString());
r = await Host(["--spark-nonsense"]);
Report("unknown --spark-* flag", r.Handled && r.Out.StartsWith("Unknown command"), r.Out);

// 4. Help lists the keys from the service descriptors, without constructing a command.
r = await Host(["--spark-help"]);
Report("--spark-help lists every key", r.Handled && new[] { "--spark-migrate", "--spark-migration-script", "--spark-synchronize-model", "--spark-help" }.All(k => r.Out.Contains(k)), r.Out.Replace("\n", " | "));
Report("--spark-help constructs no other command", r.C.Migrate == 0 && r.C.Synchronize == 0 && r.C.Script == 0, r.C.ToString());

// 5. Same key registered twice (core default, then the provider): the last registration wins.
r = await Host(["--spark-migration-script"]);
Report("provider's registration wins over core's default", r.Out == "sql script", r.Out);
r = await Host(["--spark-migration-script"], providerOverridesScript: false);
Report("core's default when no provider hooks in", r.Out == "nothing to script (no relational provider)", r.Out);

// 6. Development validates the container on Build (ValidateOnBuild + ValidateScopes): keyed scoped commands pass.
try { r = await Host(["--spark-migrate"], env: "Development"); Report("ValidateOnBuild accepts keyed scoped commands", r.Handled, "Development"); }
catch (Exception ex) { Report("ValidateOnBuild accepts keyed scoped commands", false, ex.Message); }

// 7. Resolving a scoped command from the root provider is refused under ValidateScopes: the runner must open a scope.
{
    var b = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
    b.Services.AddSparkCore();
    b.Services.AddDbContext<AppDb>(o => o.UseSqlServer("Server=tcp:10.255.255.1,1433;Database=x;Connect Timeout=5"));
    var app = b.Build();
    try { app.Services.GetRequiredKeyedService<ISparkCommand>("--spark-migrate"); Report("root resolve of a scoped command refused", false, "resolved"); }
    catch (InvalidOperationException ex) { Report("root resolve of a scoped command refused", true, ex.Message.Split('.')[0]); }
}

Console.WriteLine();
Console.WriteLine($"SUMMARY {results.Count(x => x.Ok)}/{results.Count} OK");
foreach (var x in results.Where(x => !x.Ok)) Console.WriteLine($"  FAIL {x.Check}");
return 0;

public interface ISparkCommand { Task<int> RunAsync(string[] args, TextWriter output, CancellationToken ct); }

public sealed record SparkCommandKeys(IReadOnlyList<string> Keys);

public static class SparkCommandExtensions
{
    public static IServiceCollection AddSparkCore(this IServiceCollection services)
    {
        services.AddKeyedScoped<ISparkCommand, MigrateCommand>("--spark-migrate");
        services.AddKeyedScoped<ISparkCommand, NoScriptCommand>("--spark-migration-script");
        services.AddKeyedScoped<ISparkCommand, SynchronizeCommand>("--spark-synchronize-model");
        services.AddKeyedScoped<ISparkCommand, HelpCommand>("--spark-help");
        // The key list is read from the descriptors once the container is final; no command is constructed for it.
        services.AddSingleton(_ => new SparkCommandKeys(services
            .Where(d => d.ServiceType == typeof(ISparkCommand) && d.IsKeyedService && d.ServiceKey is string)
            .Select(d => (string)d.ServiceKey!).Distinct().Order().ToList()));
        return services;
    }

    public static async Task<bool> RunSparkCommandIfRequestedAsync(this WebApplication app, string[] args, TextWriter output)
    {
        var flag = args.FirstOrDefault(a => a.StartsWith("--spark-", StringComparison.Ordinal));
        if (flag is null) return false;
        await using var scope = app.Services.CreateAsyncScope();
        var command = scope.ServiceProvider.GetKeyedService<ISparkCommand>(flag);
        if (command is null) { await output.WriteLineAsync($"Unknown command {flag}. Run --spark-help."); return true; }
        await command.RunAsync(args, output, CancellationToken.None);
        return true;
    }
}

public sealed class MigrateCommand : ISparkCommand
{
    public MigrateCommand() => Counters.Migrate++;
    public Task<int> RunAsync(string[] args, TextWriter o, CancellationToken ct) { o.WriteLine("migrate ran"); return Task.FromResult(0); }
}
public sealed class NoScriptCommand : ISparkCommand
{
    public NoScriptCommand() => Counters.Script++;
    public Task<int> RunAsync(string[] args, TextWriter o, CancellationToken ct) { o.WriteLine("nothing to script (no relational provider)"); return Task.FromResult(0); }
}
public sealed class SqlMigrationScriptCommand : ISparkCommand
{
    public SqlMigrationScriptCommand() => Counters.Script++;
    public Task<int> RunAsync(string[] args, TextWriter o, CancellationToken ct) { o.WriteLine("sql script"); return Task.FromResult(0); }
}
public sealed class HelpCommand(SparkCommandKeys keys) : ISparkCommand
{
    public Task<int> RunAsync(string[] args, TextWriter o, CancellationToken ct) { Counters.Help++; foreach (var k in keys.Keys) o.WriteLine(k); return Task.FromResult(0); }
}
// Synchronize needs the model, never the database: it builds the EF model and diffs it against "no migrations yet".
public sealed class SynchronizeCommand(AppDb db) : ISparkCommand
{
    public Task<int> RunAsync(string[] args, TextWriter o, CancellationToken ct)
    {
        Counters.Synchronize++;
        var differ = db.GetService<IMigrationsModelDiffer>();
        var model = db.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model;
        var ops = differ.GetDifferences(null, model.GetRelationalModel());
        o.WriteLine($"{ops.Count} operations: {string.Join(", ", ops.Select(x => x.GetType().Name).Distinct())}");
        return Task.FromResult(0);
    }
}

public sealed class FakeDocumentStore { public FakeDocumentStore() => Counters.Store++; }

public sealed class Person { public Guid Id { get; set; } public string Name { get; set; } = ""; }
public sealed class AppDb(DbContextOptions<AppDb> o) : DbContext(o)
{
    public DbSet<Person> People => Set<Person>();
    protected override void OnConfiguring(DbContextOptionsBuilder b) => b.AddInterceptors(new CountingInterceptor());
}
public sealed class CountingInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.DbConnectionInterceptor
{
    public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult> ConnectionOpeningAsync(System.Data.Common.DbConnection c, Microsoft.EntityFrameworkCore.Diagnostics.ConnectionEventData e, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult r, CancellationToken ct = default) { Counters.Connections++; return base.ConnectionOpeningAsync(c, e, r, ct); }
    public override Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult ConnectionOpening(System.Data.Common.DbConnection c, Microsoft.EntityFrameworkCore.Diagnostics.ConnectionEventData e, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult r) { Counters.Connections++; return base.ConnectionOpening(c, e, r); }
}

public static class Counters
{
    public static int Migrate, Script, Synchronize, Help, Store, Connections;
    public static void Reset() => Migrate = Script = Synchronize = Help = Store = Connections = 0;
    public static Snap Snapshot() => new(Migrate, Script, Synchronize, Help, Store, Connections);
}
public sealed record Snap(int Migrate, int Script, int Synchronize, int Help, int Store, int Connections)
{
    public int Total => Migrate + Script + Synchronize + Help + Store + Connections;
}
