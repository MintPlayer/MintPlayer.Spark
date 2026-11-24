// #536 spike S-ID2: Id<T> on EF Core 11 (SQL Server, PostgreSQL, InMemory). Throwaway.
// dotnet run -- <sqlserver|sqlserver2022|postgres|inmemory>      shape checks
// dotnet run -- perf-<sqlserver|postgres> [rows]                 Guid key insert cost (v4, v7, sequential, string)
// PostgreSQL: the password comes from SPARK536_PG_PASSWORD (never in this file).
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.EntityFrameworkCore.ValueGeneration;

var mode = args.Length > 0 ? args[0] : "sqlserver";
var results = new List<(string Check, bool Ok, string Detail)>();
void Report(string check, bool ok, string detail) { results.Add((check, ok, detail)); Console.WriteLine($"[{(ok ? "OK  " : "FAIL")}] {check}: {detail}"); }

if (mode.StartsWith("perf-")) { await Perf.Run(mode[5..], args.Length > 1 ? int.Parse(args[1]) : 100_000); return 0; }

var sqlLog = new List<string>();
DbContextOptions<SpikeContext> Options() => Db.Configure(new DbContextOptionsBuilder<SpikeContext>(), mode, "sparkspike536id")
    .LogTo(s => { if (s.Contains("Executed DbCommand")) sqlLog.Add(s); }, Microsoft.Extensions.Logging.LogLevel.Information)
    .Options;

await using (var ctx = new SpikeContext(Options(), mode))
{
    await ctx.Database.EnsureDeletedAsync();
    await ctx.Database.EnsureCreatedAsync();
}

// 1. Keys of each kind, PK and FK, nullable FK, join entity, child table.
var tagNet = Id<Tag>.FromTrusted("tags/dotnet");
var tagSql = Id<Tag>.FromTrusted("tags/sql");
Id<Person> annId, bobId; Id<Company> acmeId, globexId;
await using (var ctx = new SpikeContext(Options(), mode))
{
    var acme = new Company { Name = "Acme" };
    var globex = new Company { Name = "Globex" };
    ctx.Companies.Add(acme); ctx.Companies.Add(globex);
    Report("HiLo key assigned at Add (before SaveChanges)", !acme.Id.IsEmpty && !globex.Id.IsEmpty && acme.Id != globex.Id, $"{acme.Id}, {globex.Id}");
    ctx.Tags.Add(new Tag { Id = tagNet, Label = ".NET" });
    ctx.Tags.Add(new Tag { Id = tagSql, Label = "SQL" });
    var ann = new Person { Id = Id<Person>.FromTrusted($"people/{Guid.NewGuid():N}"), Name = "Ann", CompanyId = acme.Id };
    ann.Addresses.Add(new Address { Street = "Main 1" });
    ann.Addresses.Add(new Address { Street = "Side 2" });
    var bob = new Person { Id = Id<Person>.FromTrusted($"people/{Guid.NewGuid():N}"), Name = "Bob", CompanyId = null };
    ctx.People.AddRange(ann, bob);
    ctx.PersonTags.Add(new PersonTag { PersonId = ann.Id, TagId = tagNet });
    ctx.PersonTags.Add(new PersonTag { PersonId = ann.Id, TagId = tagSql });
    ctx.PersonTags.Add(new PersonTag { PersonId = bob.Id, TagId = tagNet });
    try
    {
        await ctx.SaveChangesAsync();
        Report("SaveChanges: 2 HiLo + 2 natural + 2 Guid + 3 join + 2 child rows", true, $"companies {acme.Id}, {globex.Id}");
    }
    catch (Exception ex) { Report("SaveChanges", false, Flatten(ex)); return 1; }
    annId = ann.Id; bobId = bob.Id; acmeId = acme.Id; globexId = globex.Id;
}

await using (var ctx = new SpikeContext(Options(), mode))
{
    async Task Check<T>(string name, IQueryable<T> q, Func<List<T>, bool> ok)
    {
        string sql = "";
        try { if (mode != "inmemory") sql = q.ToQueryString(); } catch (Exception ex) { sql = "ToQueryString: " + ex.Message; }
        try { var l = await q.ToListAsync(); Report(name, ok(l), OneLine(sql)); }
        catch (Exception ex) { Report(name, false, Flatten(ex)); }
    }

    var p = await ctx.People.Include(x => x.Addresses).SingleAsync(x => x.Id == annId);
    Report("Load by Guid key, Id round-trips lowercase N", p.Id == annId && p.Id.Value == p.Id.Value.ToLowerInvariant(), p.Id.Value);
    Report("Nullable converted FK reads back", p.CompanyId == acmeId, $"{p.CompanyId}");
    Report("Child table (owned) loads with the converted FK", p.Addresses.Count == 2, string.Join(", ", p.Addresses.Select(a => a.Street)));
    var found = await ctx.People.FindAsync(annId);
    Report("FindAsync(Id<T>)", found is not null, $"{found?.Id}");

    await Check("Where(x => x.Id == v)", ctx.People.Where(x => x.Id == annId), l => l.Count == 1);
    await Check("Where(x => x.CompanyId == v)", ctx.People.Where(x => x.CompanyId == acmeId), l => l.Count == 1);
    await Check("Where(x => x.CompanyId == null)", ctx.People.Where(x => x.CompanyId == null), l => l.Count == 1 && l[0].Name == "Bob");
    await Check("Natural key Where(x => x.Id == v)", ctx.Tags.Where(x => x.Id == tagNet), l => l.Count == 1);
    await Check("Join entity navigation", ctx.PersonTags.Where(x => x.TagId == tagNet).Select(x => x.Person!.Name), l => l.Count == 2);
    await Check("Reference navigation via converted FK", ctx.People.Where(x => x.Company!.Name == "Acme").Select(x => x.Name), l => l.Count == 1 && l[0] == "Ann");
    await Check("OrderBy(x => x.Id) on Guid", ctx.People.OrderBy(x => x.Id).Select(x => x.Id), l => l.Count == 2);
    await Check("OrderBy(x => x.Id) on HiLo", ctx.Companies.OrderBy(x => x.Id).Select(x => x.Id), l => l.Count == 2 && l[0] == acmeId);
    await Check("Projection Select(x => x.Id)", ctx.People.Where(x => x.Name == "Ann").Select(x => new { x.Id, x.CompanyId }), l => l.Count == 1 && l[0].Id == annId && l[0].CompanyId == acmeId);

    foreach (var n in new[] { 0, 1, 50, 2000, 5000 })
    {
        var ids = Enumerable.Range(0, n).Select(i => i == 0 ? annId : Id<Person>.FromTrusted($"people/{Guid.NewGuid():N}")).ToList();
        await Check($"Contains over {n} Guid ids", ctx.People.Where(x => ids.Contains(x.Id)), l => l.Count == (n == 0 ? 0 : 1));
    }
    var cids = new List<Id<Company>> { acmeId, globexId };
    await Check("Contains over HiLo ids", ctx.Companies.Where(x => cids.Contains(x.Id)), l => l.Count == 2);
    var ncids = new List<Id<Company>?> { acmeId };
    await Check("Contains over nullable FK values", ctx.People.Where(x => ncids.Contains(x.CompanyId)), l => l.Count == 1);
    var tids = new List<Id<Tag>> { tagNet };
    await Check("Contains over natural ids", ctx.Tags.Where(x => tids.Contains(x.Id)), l => l.Count == 1);
}

// Upper-case input never reaches the database: Id<T> lowers it, the Guid parser accepts it.
await using (var ctx = new SpikeContext(Options(), mode))
{
    var upper = Id<Person>.FromTrusted(annId.Value.ToUpperInvariant());
    var hit = await ctx.People.AnyAsync(x => x.Id == upper);
    Report("Upper-case id input finds the row", hit, upper.Value);
    var badFk = new Person { Id = Id<Person>.FromTrusted($"people/{Guid.NewGuid():N}"), Name = "Dangling", CompanyId = Id<Company>.FromTrusted("companies/999999") };
    ctx.People.Add(badFk);
    try { await ctx.SaveChangesAsync(); Report("FK violation on a dangling converted reference", mode == "inmemory", "saved (InMemory has no FKs)"); }
    catch (DbUpdateException ex) { Report("FK violation on a dangling converted reference", mode != "inmemory", Flatten(ex)); }
}

Console.WriteLine();
Console.WriteLine($"SUMMARY [{mode}] {results.Count(r => r.Ok)}/{results.Count} OK");
foreach (var r in results.Where(r => !r.Ok)) Console.WriteLine($"  FAIL {r.Check}");
return 0;

static string Flatten(Exception ex) { var m = ex.GetType().Name + ": " + ex.Message; for (var i = ex.InnerException; i is not null; i = i.InnerException) m += " | " + i.Message; return OneLine(m); }
static string OneLine(string s) { s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " "); return s.Length > 400 ? s[..120] + " … " + s[^280..] : s; }

public static class Db
{
    public static DbContextOptionsBuilder<T> Configure<T>(DbContextOptionsBuilder<T> b, string mode, string database) where T : DbContext => mode switch
    {
        "sqlserver" => b.UseSqlServer($@"Server=localhost\MSSQLSERVER01;Database={database};Trusted_Connection=True;TrustServerCertificate=True"),
        "sqlserver2022" => b.UseSqlServer($"Server=localhost;Database={database};Trusted_Connection=True;TrustServerCertificate=True"),
        "postgres" => b.UseNpgsql($"Host=127.0.0.1;Port=55432;Username=postgres;Password={Environment.GetEnvironmentVariable("SPARK536_PG_PASSWORD")};Database={database}"),
        "inmemory" => b.UseInMemoryDatabase(database),
        _ => throw new ArgumentException(mode),
    };
}

public readonly struct Id<T> : IEquatable<Id<T>>, IComparable<Id<T>>, IComparable
{
    private readonly string? value;
    private Id(string v) => value = v;
    public string Value => value ?? "";
    public bool IsEmpty => value is null;
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

// What the generator would emit per kind. The prefix is fixed at compile time (D23 rule 4).
public static class IdCodec
{
    public static long ToLong<T>(Id<T> id, string prefix) => long.Parse(id.Value.AsSpan(prefix.Length + 1));
    public static Id<T> FromLong<T>(long v, string prefix) => Id<T>.FromTrusted($"{prefix}/{v}");
    public static Guid ToGuid<T>(Id<T> id, string prefix) => Guid.ParseExact(id.Value.AsSpan(prefix.Length + 1), "N");
    public static Id<T> FromGuid<T>(Guid v, string prefix) => Id<T>.FromTrusted($"{prefix}/{v:N}");
    public static string ToKey<T>(Id<T> id, string prefix) => id.Value[(prefix.Length + 1)..];
    public static Id<T> FromKey<T>(string v, string prefix) => Id<T>.FromTrusted($"{prefix}/{v}");
}
public sealed class LongIdConverter<T>(string prefix) : ValueConverter<Id<T>, long>(id => IdCodec.ToLong(id, prefix), v => IdCodec.FromLong<T>(v, prefix));
public sealed class GuidIdConverter<T>(string prefix) : ValueConverter<Id<T>, Guid>(id => IdCodec.ToGuid(id, prefix), v => IdCodec.FromGuid<T>(v, prefix));
public sealed class KeyIdConverter<T>(string prefix) : ValueConverter<Id<T>, string>(id => IdCodec.ToKey(id, prefix), v => IdCodec.FromKey<T>(v, prefix));

// HiLo on a converted key: EF's generator yields long; this wraps it into Id<T>.
public sealed class HiLoIdGenerator<T>(string prefix) : ValueGenerator<Id<T>>
{
    private static long next = 0; // stand-in when the provider has no sequence (InMemory)
    public override bool GeneratesTemporaryValues => false;
    public override Id<T> Next(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry) => IdCodec.FromLong<T>(Interlocked.Increment(ref next), prefix);
}

public class Person
{
    public Id<Person> Id { get; set; }
    public string Name { get; set; } = "";
    public Id<Company>? CompanyId { get; set; }
    public Company? Company { get; set; }
    public List<Address> Addresses { get; set; } = [];
}
public class Address { public string Street { get; set; } = ""; }
public class Company { public Id<Company> Id { get; set; } public string Name { get; set; } = ""; }
public class Tag { public Id<Tag> Id { get; set; } public string Label { get; set; } = ""; }
public class PersonTag
{
    public Id<Person> PersonId { get; set; }
    public Person? Person { get; set; }
    public Id<Tag> TagId { get; set; }
    public Tag? Tag { get; set; }
}

public class SpikeContext(DbContextOptions<SpikeContext> options, string mode) : DbContext(options)
{
    public DbSet<Person> People => Set<Person>();
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<PersonTag> PersonTags => Set<PersonTag>();

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<Company>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasConversion(new LongIdConverter<Company>("companies"));
            if (mode == "inmemory") e.Property(x => x.Id).HasValueGenerator((_, _) => new HiLoIdGenerator<Company>("companies"));
            else if (mode.StartsWith("sqlserver")) Microsoft.EntityFrameworkCore.SqlServerPropertyBuilderExtensions.UseHiLo(e.Property(x => x.Id), "companies_hilo");
            else Microsoft.EntityFrameworkCore.NpgsqlPropertyBuilderExtensions.UseHiLo(e.Property(x => x.Id), "companies_hilo");
        });
        m.Entity<Tag>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasConversion(new KeyIdConverter<Tag>("tags")).HasMaxLength(400).ValueGeneratedNever();
            if (mode.StartsWith("sqlserver")) e.Property(x => x.Id).UseCollation("Latin1_General_100_BIN2");
        });
        m.Entity<Person>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasConversion(new GuidIdConverter<Person>("people")).ValueGeneratedNever();
            e.Property(x => x.CompanyId).HasConversion(new LongIdConverter<Company>("companies"));
            e.HasOne(x => x.Company).WithMany().HasForeignKey(x => x.CompanyId);
            e.OwnsMany(x => x.Addresses, a => { a.ToTable("PersonAddresses"); a.WithOwner().HasForeignKey("PersonId"); });
        });
        m.Entity<PersonTag>(e =>
        {
            e.HasKey(x => new { x.PersonId, x.TagId });
            e.Property(x => x.PersonId).HasConversion(new GuidIdConverter<Person>("people"));
            e.Property(x => x.TagId).HasConversion(new KeyIdConverter<Tag>("tags")).HasMaxLength(400);
            if (mode.StartsWith("sqlserver")) e.Property(x => x.TagId).UseCollation("Latin1_General_100_BIN2");
            e.HasOne(x => x.Person).WithMany().HasForeignKey(x => x.PersonId);
            e.HasOne(x => x.Tag).WithMany().HasForeignKey(x => x.TagId);
        });
    }
}

// Insert cost of Guid key flavours over N rows, in EF batches of 1000.
public static class Perf
{
    public sealed class Row { public Guid Id { get; set; } public string Name { get; set; } = ""; }
    public sealed class StrRow { public string Id { get; set; } = ""; public string Name { get; set; } = ""; }
    public sealed class PerfCacheKey : Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory { public object Create(DbContext c, bool designTime) => c is PerfContext p ? (p.Table, p.Str, designTime) : c.GetType(); }
    public sealed class PerfContext(DbContextOptions<PerfContext> o, string table, bool str) : DbContext(o)
    {
        public string Table => table;
        public bool Str => str;
        protected override void OnModelCreating(ModelBuilder m)
        {
            if (str) m.Entity<StrRow>(e => { e.ToTable(table); e.HasKey(x => x.Id); e.Property(x => x.Id).HasMaxLength(450).IsUnicode(false); e.Property(x => x.Id).ValueGeneratedNever(); });
            else m.Entity<Row>(e => { e.ToTable(table); e.HasKey(x => x.Id); e.Property(x => x.Id).ValueGeneratedNever(); });
        }
    }

    public static async Task Run(string provider, int rows)
    {
        var seq = new SequentialGuidValueGenerator();
        var flavours = new List<(string Name, bool Str, Func<Guid> Make)>
        {
            ("v4", false, Guid.NewGuid),
            ("v7", false, Guid.CreateVersion7),
            ("v4-string", true, Guid.NewGuid),
        };
        if (provider == "sqlserver") flavours.Insert(2, ("ef-sequential", false, () => seq.Next(null!)));
        var runs = new List<(string Name, bool Str, Func<Guid> Make)> { ("warmup", false, Guid.NewGuid) }; runs.AddRange(flavours); runs.AddRange(Enumerable.Reverse(flavours));
        foreach (var f in runs)
        {
            var table = "perf_" + f.Name.Replace("-", "_");
            var dbName = "sparkspike536perf";
            DbContextOptions<PerfContext> o = Db.Configure(new DbContextOptionsBuilder<PerfContext>(), provider, dbName)
                .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, PerfCacheKey>().Options;
            await using (var c = new PerfContext(o, table, f.Str)) { await c.Database.EnsureDeletedAsync(); await c.Database.EnsureCreatedAsync(); }
            var sw = Stopwatch.StartNew();
            for (var done = 0; done < rows; done += 1000)
            {
                await using var c = new PerfContext(o, table, f.Str);
                for (var i = 0; i < 1000; i++)
                {
                    var g = f.Make();
                    if (f.Str) c.Add(new StrRow { Id = $"people/{g:N}", Name = "n" + i });
                    else c.Add(new Row { Id = g, Name = "n" + i });
                }
                await c.SaveChangesAsync();
            }
            sw.Stop();
            await using var cc = new PerfContext(o, table, f.Str);
            var conn = cc.Database.GetDbConnection();
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = provider == "sqlserver"
                ? $"SELECT CONCAT(s.page_count, ' pages, ', CAST(s.avg_fragmentation_in_percent AS decimal(5,1)), '% fragmented, pages ', CAST(s.avg_page_space_used_in_percent AS decimal(5,1)), '% full') FROM sys.dm_db_index_physical_stats(DB_ID(), OBJECT_ID('{table}'), 1, NULL, 'DETAILED') s WHERE s.index_level = 0"
                : $"SELECT pg_size_pretty(pg_relation_size('\"PK_{table}\"')) || ' pk index, ' || pg_size_pretty(pg_relation_size('\"{table}\"')) || ' heap'";
            var stats = await cmd.ExecuteScalarAsync();
            Console.WriteLine($"PERF [{provider}] {f.Name,-14} {rows} rows in {sw.Elapsed.TotalSeconds,6:F1} s; {stats}");
        }
    }
}
