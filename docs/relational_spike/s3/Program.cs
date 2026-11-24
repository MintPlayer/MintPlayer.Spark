// #536 spike S3: the model synchronize generates EF migration files (PRD D24) from a model built from metadata,
// through EF's public design-time services, with no `dotnet ef` tool and no design-time DbContext factory. Throwaway.
//
// dotnet run -- gen <n>      scaffold migration V<n> for model version n (both providers) into Migrations/<Provider>/
// dotnet run -- verify <n>   after the rebuild that compiles V<n>: nothing pending, migrate, exercise the new shape
// dotnet run -- reset        drop both databases and delete Migrations/
// PostgreSQL password from SPARK536_PG_PASSWORD.
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;
using S3;

var cmd = args.Length > 0 ? args[0] : "gen";
var version = args.Length > 1 ? int.Parse(args[1]) : 1;
Meta.Version = version;
var projectDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
var results = new List<(string Check, bool Ok, string Detail)>();
void Report(string check, bool ok, string detail) { results.Add((check, ok, detail)); Console.WriteLine($"[{(ok ? "OK  " : "FAIL")}] {check}: {detail}"); }

AppDb NewDb(string provider) => provider == "SqlServer" ? new SqlServerAppDb() : new PostgreSqlAppDb();

if (cmd == "reset")
{
    foreach (var p in new[] { "SqlServer", "PostgreSql" }) { await using var db = NewDb(p); await db.Database.EnsureDeletedAsync(); }
    var dir = Path.Combine(projectDir, "Migrations");
    if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    Console.WriteLine("reset");
    return 0;
}

foreach (var provider in new[] { "SqlServer", "PostgreSql" })
{
    await using var db = NewDb(provider);
    if (cmd == "gen")
    {
        // What synchronize does: compare the model built from metadata with the compiled snapshot.
        var pending = db.Database.HasPendingModelChanges();
        if (!pending) { Report($"{provider} v{version}: no model change, nothing written", true, "HasPendingModelChanges() = false"); continue; }

        if (version >= 2)
        {
            // The trap: the check above finalized this context's snapshot model, and EF 11's
            // SnapshotModelProcessor.Process returns null for a read-only model, so scaffolding on the SAME
            // context diffs against nothing and recreates every table.
            var (trapScaffolder, trapSp) = Scaffolder(db, new CollectingReporter());
            var trap = trapScaffolder.ScaffoldMigration("Trap", "S3", $"Migrations.{provider}", "C#");
            Report($"{provider} v{version}: scaffolding on the context that ran HasPendingModelChanges recreates every table (trap)", trap.MigrationCode.Split("protected override void Down")[0].Contains("name: \"Companies\""), "reproduced: its Up creates Companies again");
            await trapSp.DisposeAsync();
        }

        // A fresh context: its own scoped IMigrationsAssembly, so a fresh, mutable snapshot model.
        await using var scaffoldDb = NewDb(provider);
        var reporter = new CollectingReporter();
        var (scaffolder, sp) = Scaffolder(scaffoldDb, reporter);
        await using var _sp = sp;
        var name = $"V{version}_{Meta.Label(version)}";
        var migration = scaffolder.ScaffoldMigration(name, rootNamespace: "S3", subNamespace: $"Migrations.{provider}", language: "C#");
        var files = scaffolder.Save(projectDir, migration, Path.Combine(projectDir, "Migrations", provider));
        var up = migration.MigrationCode.Split("protected override void Down")[0];
        var ops = Regex.Matches(up, @"migrationBuilder\.(\w+)(?:<\w+>)?\(\s*(?:name: ""(\w+)"")?").Select(m => m.Groups[1].Value + (m.Groups[2].Success ? $"({m.Groups[2].Value})" : "")).ToList();
        Report($"{provider} v{version}: scaffolded {migration.MigrationId}", true, string.Join(", ", ops));
        foreach (var w in reporter.Warnings) Report($"{provider} v{version}: reporter warning", true, w);
        File.WriteAllText(Path.Combine(projectDir, $"last-up-{provider}.txt"), up);
        _ = files;
    }
    else if (cmd == "verify")
    {
        Report($"{provider} v{version}: nothing pending after the rebuild", !db.Database.HasPendingModelChanges(), "HasPendingModelChanges() = false");
        var all = db.Database.GetMigrations().ToList();
        await db.Database.MigrateAsync();
        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
        Report($"{provider} v{version}: migrated", applied.Count == all.Count && all.Count == version, $"{applied.Count}/{all.Count}: {string.Join(", ", applied)}");
        try { await Exercise(db, provider); }
        catch (Exception ex) { Report($"{provider} v{version}: exercise", false, Flatten(ex)); }
    }
}

if (cmd == "verify" && version == 3)
{
    // Why a queryable string attribute needs a length on SQL Server: an index on nvarchar(max) is refused.
    await using var db = NewDb("SqlServer");
    try
    {
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE S3MaxProbe (V nvarchar(max)); CREATE INDEX IX_S3MaxProbe ON S3MaxProbe (V);");
        Report("SqlServer: index on nvarchar(max)", false, "accepted");
    }
    catch (Exception ex) { Report("SqlServer: index on nvarchar(max) refused", true, ex.Message.Split('\n')[0]); }
    finally { await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS S3MaxProbe"); }
}

Console.WriteLine();
Console.WriteLine($"SUMMARY {cmd} v{version}: {results.Count(r => r.Ok)}/{results.Count} OK");
foreach (var r in results.Where(r => !r.Ok)) Console.WriteLine($"  FAIL {r.Check}");
return 0;

async Task Exercise(AppDb db, string provider)
{
    var tag = $"{provider} v{version}";
    var acme = new Company { Id = Id<Company>.FromTrusted($"companies/{Guid.NewGuid():N}"), Name = "Acme" };
    db.Add(acme);
    var p = new Person { Id = Id<Person>.FromTrusted($"people/{Guid.NewGuid():N}"), FirstName = "Ann", LastName = "Keep", Surname = "Keep", Email = "ann@example.com" };
    if (version >= 4) p.Addresses.Add(new Address { Id = "a1", Street = "Main 1", City = "Gent" });
    if (version >= 5) p.CompanyId = acme.Id;
    db.Add(p);
    await db.SaveChangesAsync();
    db.ChangeTracker.Clear();

    var back = await db.Set<Person>().SingleAsync(x => x.Id == p.Id);
    Report($"{tag}: round trip", back.FirstName == "Ann", $"FirstName {back.FirstName}");
    if (version >= 2) Report($"{tag}: the added field round-trips", back.Email == "ann@example.com", $"Email {back.Email}");
    if (version >= 3)
    {
        var byEmail = await db.Set<Person>().Where(x => x.Email == "ann@example.com").CountAsync();
        var hasIndex = db.Model.FindEntityType(typeof(Person))!.GetIndexes().Any(i => i.Properties.Any(x => x.Name == "Email"));
        Report($"{tag}: the queryable field is indexed and filters", hasIndex && byEmail >= 1, $"index {hasIndex}, {byEmail} match");
    }
    if (version >= 4)
    {
        Report($"{tag}: the AsDetail collection round-trips", back.Addresses.Count == 1 && back.Addresses[0].City == "Gent", $"{back.Addresses.Count} address(es)");
        db.Remove(back);
        await db.SaveChangesAsync();
        var orphans = await db.Database.SqlQueryRaw<int>($"SELECT COUNT(*) AS \"Value\" FROM {Q(provider, "PersonAddresses")}").SingleAsync();
        var remaining = await db.Set<Person>().CountAsync(x => x.Id == p.Id);
        Report($"{tag}: deleting the owner deletes its child rows", remaining == 0 && orphans == 0, $"{orphans} orphan rows");
    }
    if (version >= 5)
    {
        var q = new Person { Id = Id<Person>.FromTrusted($"people/{Guid.NewGuid():N}"), FirstName = "Bob", CompanyId = acme.Id };
        db.Add(q); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var atAcme = await db.Set<Person>().Where(x => x.CompanyId == acme.Id).CountAsync();
        Report($"{tag}: the reference is a real FK and filters", atAcme == 1, $"{atAcme} at Acme");
        db.Add(new Person { Id = Id<Person>.FromTrusted($"people/{Guid.NewGuid():N}"), FirstName = "Dangling", CompanyId = Id<Company>.FromTrusted($"companies/{Guid.NewGuid():N}") });
        try { await db.SaveChangesAsync(); Report($"{tag}: a dangling reference is refused", false, "saved"); }
        catch (DbUpdateException ex) { Report($"{tag}: a dangling reference is refused", true, (ex.InnerException?.Message ?? ex.Message).Split('\n')[0]); }
        db.ChangeTracker.Clear();
        if (version == 5)
        {
            db.Add(new Person { Id = Id<Person>.FromTrusted("people/0000000000000000000000000000beef"), FirstName = "Old", LastName = "Kept through the rename" });
            await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        }
    }
    if (version >= 6)
    {
        var old = await db.Set<Person>().SingleOrDefaultAsync(x => x.Id == Id<Person>.FromTrusted("people/0000000000000000000000000000beef"));
        Report($"{tag}: the renamed field kept its data", old?.Surname == "Kept through the rename", $"Surname '{old?.Surname}'");
    }
}

// Design-time services composed from public API only: no `dotnet ef`, no IDesignTimeDbContextFactory.
static (IMigrationsScaffolder, ServiceProvider) Scaffolder(DbContext db, IOperationReporter reporter)
{
    var services = new ServiceCollection();
    services.AddEntityFrameworkDesignTimeServices(reporter);
    services.AddDbContextDesignTimeServices(db);
    // The provider's design-time services, found through the public attribute on its assembly (what the EF tools
    // do), so no pubternal provider type is named here.
    var providerAssembly = Assembly.Load(db.Database.ProviderName!);
    var attr = providerAssembly.GetCustomAttribute<DesignTimeProviderServicesAttribute>()!;
    var designTime = (IDesignTimeServices)Activator.CreateInstance(providerAssembly.GetType(attr.TypeName, throwOnError: true)!)!;
    designTime.ConfigureDesignTimeServices(services);
    var sp = services.BuildServiceProvider();
    return (sp.GetRequiredService<IMigrationsScaffolder>(), sp);
}

static string Q(string provider, string id) => provider == "SqlServer" ? $"[{id}]" : $"\"{id}\"";
static string Flatten(Exception ex) { var m = ex.GetType().Name + ": " + ex.Message.Split('\n')[0]; return ex.InnerException is null ? m : m + " <- " + Flatten(ex.InnerException); }

namespace S3
{
    // ---- The "Spark model": metadata, not attributes on classes. Each version is what the model JSON says. ----
    public sealed record AttrMeta(string Name, bool Queryable = false, int? MaxLength = null, string? RenamedFrom = null);
    public sealed record DetailMeta(string Name, Type Clr, string Table, AttrMeta[] Attributes);
    public sealed record RefMeta(string Name, Type Target);
    public sealed record EntityMeta(Type Clr, string Table, string Prefix, AttrMeta[] Attributes, DetailMeta[] Details, RefMeta[] References);

    public static class Meta
    {
        public static int Version = 1;
        public static string Label(int v) => v switch { 1 => "Initial", 2 => "AddEmail", 3 => "EmailQueryable", 4 => "AddAddresses", 5 => "AddCompanyReference", 6 => "RenameLastName", _ => "V" + v };
        public static EntityMeta[] Model(int v)
        {
            var person = new List<AttrMeta> { new("FirstName") };
            person.Add(v >= 6 ? new AttrMeta("Surname", RenamedFrom: "LastName") : new AttrMeta("LastName"));
            if (v == 2) person.Add(new("Email"));
            if (v >= 3) person.Add(new("Email", Queryable: true, MaxLength: 256));
            var details = v >= 4 ? new[] { new DetailMeta("Addresses", typeof(Address), "PersonAddresses", [new("Street"), new("City")]) } : [];
            var refs = v >= 5 ? new[] { new RefMeta("CompanyId", typeof(Company)) } : [];
            return
            [
                new(typeof(Company), "Companies", "companies", [new("Name")], [], []),
                new(typeof(Person), "People", "people", [.. person], details, refs),
            ];
        }
    }

    public abstract class AppDb : DbContext
    {
        protected override void OnModelCreating(ModelBuilder mb)
        {
            foreach (var e in Meta.Model(Meta.Version))
            {
                var eb = mb.Entity(e.Clr);
                eb.ToTable(e.Table);
                eb.HasKey("Id");
                eb.Property("Id").HasConversion(IdConverter(e.Clr, e.Prefix)).ValueGeneratedNever();
                var mapped = new HashSet<string>(e.Attributes.Select(a => a.Name).Concat(e.Details.Select(d => d.Name)).Concat(e.References.Select(r => r.Name))) { "Id" };
                foreach (var p in e.Clr.GetProperties().Where(p => !mapped.Contains(p.Name))) eb.Ignore(p.Name);
                foreach (var a in e.Attributes)
                {
                    var pb = eb.Property(a.Name);
                    if (a.MaxLength is { } len) pb.HasMaxLength(len);
                    if (a.Queryable) eb.HasIndex(a.Name);
                }
                foreach (var d in e.Details)
                {
                    // D25: a child table keyed by (owner id, child id), FK to the owner, cascade delete.
                    var ownerFk = e.Clr.Name + "Id";
                    eb.OwnsMany(d.Clr, d.Name, ob =>
                    {
                        ob.ToTable(d.Table);
                        ob.Property(typeof(Id<>).MakeGenericType(e.Clr), ownerFk).HasConversion(IdConverter(e.Clr, e.Prefix));
                        ob.WithOwner().HasForeignKey(ownerFk);
                        ob.HasKey(ownerFk, "Id");
                        var dm = new HashSet<string>(d.Attributes.Select(a => a.Name)) { "Id" };
                        foreach (var p in d.Clr.GetProperties().Where(p => !dm.Contains(p.Name))) ob.Ignore(p.Name);
                    });
                }
                foreach (var r in e.References)
                {
                    // D26: a real FK, no navigation property.
                    var target = Meta.Model(Meta.Version).Single(x => x.Clr == r.Target);
                    eb.Property(r.Name).HasConversion(IdConverter(r.Target, target.Prefix));
                    eb.HasOne(r.Target).WithMany().HasForeignKey(r.Name).OnDelete(DeleteBehavior.Restrict);
                }
            }
        }

        static ValueConverter IdConverter(Type clr, string prefix) => (ValueConverter)Activator.CreateInstance(typeof(GuidIdConverter<>).MakeGenericType(clr), prefix)!;
    }

    public sealed class SqlServerAppDb : AppDb
    {
        protected override void OnConfiguring(DbContextOptionsBuilder b) => b.UseSqlServer(@"Server=localhost\MSSQLSERVER01;Database=sparkspike536s3;Trusted_Connection=True;TrustServerCertificate=True");
    }
    public sealed class PostgreSqlAppDb : AppDb
    {
        protected override void OnConfiguring(DbContextOptionsBuilder b) => b.UseNpgsql($"Host=127.0.0.1;Port=55432;Username=postgres;Password={Environment.GetEnvironmentVariable("SPARK536_PG_PASSWORD")};Database=sparkspike536s3");
    }

    public sealed class CollectingReporter : IOperationReporter
    {
        public List<string> Warnings { get; } = [];
        public void WriteError(string message) => Warnings.Add("error: " + message);
        public void WriteWarning(string message) => Warnings.Add(message);
        public void WriteInformation(string message) { }
        public void WriteVerbose(string message) { }
    }

    // ---- Entities: CLR classes carry every property any version maps; the metadata decides what is mapped. ----
    public sealed class Company { public Id<Company> Id { get; set; } public string Name { get; set; } = ""; }
    public sealed class Person
    {
        public Id<Person> Id { get; set; }
        public string FirstName { get; set; } = "";
        public string? LastName { get; set; }
        public string? Surname { get; set; }
        public string? Email { get; set; }
        public List<Address> Addresses { get; set; } = [];
        public Id<Company>? CompanyId { get; set; }
    }
    public sealed class Address { public string Id { get; set; } = ""; public string Street { get; set; } = ""; public string City { get; set; } = ""; }

    // ---- Id<T> as in S-ID2 (D23). ----
    public readonly struct Id<T> : IEquatable<Id<T>>, IComparable<Id<T>>, IComparable
    {
        private readonly string? value;
        private Id(string v) => value = v;
        public string Value => value ?? "";
        public static Id<T> FromTrusted(string v) => new(v.ToLowerInvariant());
        public bool Equals(Id<T> other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
        public override bool Equals(object? obj) => obj is Id<T> o && Equals(o);
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);
        public static bool operator ==(Id<T> a, Id<T> b) => a.Equals(b);
        public static bool operator !=(Id<T> a, Id<T> b) => !a.Equals(b);
        public override string ToString() => Value;
        public int CompareTo(Id<T> other) => string.CompareOrdinal(Value, other.Value);
        public int CompareTo(object? obj) => obj is Id<T> o ? CompareTo(o) : 1;
    }
    public static class IdCodec
    {
        public static Guid ToGuid<T>(Id<T> id, string prefix) => Guid.ParseExact(id.Value.AsSpan(prefix.Length + 1), "N");
        public static Id<T> FromGuid<T>(Guid v, string prefix) => Id<T>.FromTrusted($"{prefix}/{v:N}");
    }
    public sealed class GuidIdConverter<T>(string prefix) : ValueConverter<Id<T>, Guid>(id => IdCodec.ToGuid(id, prefix), v => IdCodec.FromGuid<T>(v, prefix));
}
