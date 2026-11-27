// #536 spikes S1/S2: can EF Core 11 carry Spark's document model and its query shapes on SQL Server?
// C1 = relational table per type (scalars as columns, nested values as JSON columns).
// C2 = document table: Doc { Id, Collection, Data (the whole entity as ONE complex JSON column) },
//      with T-lambdas rewritten onto d.Data.
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

var server = args.Length > 0 ? args[0] : @"localhost\MSSQLSERVER01";
var compat = args.Length > 1 ? int.Parse(args[1]) : 170;
var cs = $"Server={server};Database=SparkSpike536;Trusted_Connection=True;TrustServerCertificate=True";
Console.WriteLine($"== server {server}, compat {compat}");

if (args.Contains("r2")) { await Round2.RunAsync(cs, compat); return; }
if (args.Contains("r3")) { await Round3.RunAsync(cs, compat); return; }

var people = Seed.People();
var queries = Seed.Queries();

foreach (var variant in new[] { "C1", "C2", "C3" })
{
    Console.WriteLine($"\n######## {variant}");
    try
    {
        await using var db = new SpikeDb(cs, variant, compat);
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();
        Console.WriteLine("model+schema OK");
        Console.WriteLine(db.Database.GenerateCreateScript().Split("GO")[0].Trim());

        if (variant == "C3")
        {
            db.Set<Titled>().Add(new Titled { Id = "titled/1", Title = new() { Translations = { ["en"] = "Hello", ["nl"] = "Hallo" } } });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var t = await db.Set<Titled>().Where(x => x.Title!.Translations["nl"] == "Hallo").ToListAsync();
            Console.WriteLine($"C3 dictionary query rows={t.Count} nl={t.FirstOrDefault()?.Title?.Translations["nl"]}");
            continue;
        }

        if (variant == "C1") db.Set<Person>().AddRange(people);
        else db.Set<Doc<Person>>().AddRange(people.Select(p => new Doc<Person> { Id = p.Id!, Collection = "People", Data = p }));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        foreach (var (name, predicate, order) in queries)
        {
            try
            {
                IQueryable<Person> q;
                if (variant == "C1")
                {
                    q = db.Set<Person>().Where(predicate);
                    if (order is not null) q = q.OrderBy(order).Skip(0).Take(10);
                }
                else
                {
                    var docs = db.Set<Doc<Person>>().Where(d => d.Collection == "People").Where(Rewrite.OntoData(predicate));
                    if (order is not null) docs = docs.OrderBy(Rewrite.OntoData(order)).Skip(0).Take(10);
                    q = docs.Select(d => d.Data);
                }
                var sql = q.ToQueryString();
                var rows = await q.ToListAsync();
                Console.WriteLine($"OK   {name,-28} rows={rows.Count} [{string.Join(",", rows.Select(r => r.FirstName))}]");
                Console.WriteLine("     " + sql.Replace("\r", "").Replace("\n", " ").Replace("  ", " "));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAIL {name,-28} {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
            }
        }

        // Concurrency: rowversion round trip and a stale write.
        var first = variant == "C1"
            ? (object)await db.Set<Person>().FirstAsync(p => p.Id == "people/1")
            : await db.Set<Doc<Person>>().FirstAsync(p => p.Id == "people/1");
        var version = Convert.ToBase64String((byte[])db.Entry(first).Property("Version").CurrentValue!);
        Console.WriteLine($"version people/1 = {version}");
        await db.Database.ExecuteSqlRawAsync($"UPDATE {(variant == "C1" ? "People" : "Docs")} SET Collection = Collection WHERE Id = 'people/1'");
        if (first is Person p1) p1.Email = "changed@example.org"; else ((Doc<Person>)first).Data.Email = "changed@example.org";
        try { await db.SaveChangesAsync(); Console.WriteLine("stale write ACCEPTED (bad)"); }
        catch (DbUpdateConcurrencyException) { Console.WriteLine("stale write refused: DbUpdateConcurrencyException (good)"); }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"VARIANT FAIL {ex.GetType().Name}: {ex.Message}");
    }
}

// ---------- model ----------

public class Person
{
    public string? Id { get; set; }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string FullName => $"{FirstName} {LastName}";   // get-only, persisted by RavenDB
    public string? Email { get; set; }
    public DateOnly? DateOfBirth { get; set; }
    public DateTimeOffset? Hired { get; set; }
    public string? Company { get; set; }
    public List<string> Professions { get; set; } = [];
    public Address? Address { get; set; }
    public List<CarreerJob> Jobs { get; set; } = [];
    public Status Status { get; set; }
}
public class Address { public string Street { get; set; } = ""; public string City { get; set; } = ""; }
public class CarreerJob { public string? ProfessionId { get; set; } public DateOnly ContractStart { get; set; } }
public enum Status { Active, Retired }
public class TranslatedString { public Dictionary<string, string> Translations { get; set; } = new(); }
public class Titled { public string Id { get; set; } = ""; public TranslatedString? Title { get; set; } }
public class Doc<T> where T : class { public string Id { get; set; } = ""; public string Collection { get; set; } = ""; public T Data { get; set; } = default!; }

public class SpikeDb(string cs, string variant, int compat) : DbContext
{
    protected override void OnConfiguring(DbContextOptionsBuilder o)
        => o.UseSqlServer(cs, s => s.UseCompatibilityLevel(compat))
            .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, VariantKey>();

    public string Variant => variant;

    private sealed class VariantKey : Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) => (((SpikeDb)context).Variant, designTime);
    }

    protected override void OnModelCreating(ModelBuilder mb)
    {
        if (variant == "C1")
        {
            mb.Entity<Person>(b =>
            {
                b.ToTable("People");
                b.HasKey(p => p.Id);
                b.Property<string>("Collection").HasDefaultValue("People");
                b.Property<byte[]>("Version").IsRowVersion();
                b.ComplexProperty(p => p.Address, a => a.ToJson());
                b.ComplexCollection(p => p.Jobs, j => j.ToJson());
                b.Property(p => p.Status).HasConversion<string>();
            });
        }
        else if (variant == "C2")
        {
            mb.Entity<Doc<Person>>(b =>
            {
                b.ToTable("Docs");
                b.HasKey(d => d.Id);
                b.Property<byte[]>("Version").IsRowVersion();
                b.ComplexProperty(d => d.Data, data =>
                {
                    data.ToJson();
                    data.ComplexProperty(p => p.Address);
                    data.ComplexCollection(p => p.Jobs);
                    data.Property(p => p.Status).HasConversion<string>();
                });
            });
        }
        else
        {
            mb.Entity<Titled>(b => b.ComplexProperty(t => t.Title, t => t.ToJson()));
        }
    }
}

public static class Rewrite
{
    /// <summary>x => body(x) on T  ==>  d => body(d.Data) on Doc&lt;T&gt;.</summary>
    public static Expression<Func<Doc<T>, TResult>> OntoData<T, TResult>(Expression<Func<T, TResult>> lambda) where T : class
    {
        var d = Expression.Parameter(typeof(Doc<T>), "d");
        var body = new Swap(lambda.Parameters[0], Expression.Property(d, nameof(Doc<T>.Data))).Visit(lambda.Body);
        return Expression.Lambda<Func<Doc<T>, TResult>>(body, d);
    }
    private sealed class Swap(ParameterExpression from, Expression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }
}

public static class Seed
{
    public static List<Person> People() =>
    [
        new() { Id = "people/1", FirstName = "Ann", LastName = "Smith", Email = "ann@example.org", DateOfBirth = new(1980, 1, 2),
                Hired = new DateTimeOffset(2020, 1, 1, 9, 0, 0, TimeSpan.FromHours(2)), Company = "companies/1",
                Professions = ["professions/1", "professions/2"], Address = new() { Street = "Main 1", City = "Ghent" },
                Jobs = [new() { ProfessionId = "professions/1", ContractStart = new(2020, 1, 1) }] },
        new() { Id = "people/2", FirstName = "Bob", LastName = "Jones", Company = "companies/2", Status = Status.Retired,
                Hired = new DateTimeOffset(2020, 1, 1, 8, 30, 0, TimeSpan.Zero), Professions = ["professions/3"] },
        new() { Id = "people/3", FirstName = "Cleo", LastName = "Smith", Email = "cleo@example.org",
                Address = new() { Street = "Side 2", City = "Antwerp" },
                Jobs = [new() { ProfessionId = "professions/2", ContractStart = new(2019, 5, 1) }] },
    ];

    static (string, Expression<Func<Person, bool>>, Expression<Func<Person, string>>?) Q(string n, Expression<Func<Person, bool>> p, Expression<Func<Person, string>>? o = null) => (n, p, o);

    public static List<(string Name, Expression<Func<Person, bool>> Predicate, Expression<Func<Person, string>>? Order)> Queries()
    {
        var ids = new List<string> { "people/1", "people/3" };
        var wanted = new[] { "professions/2" };
        var hiredAfter = new DateTimeOffset(2020, 1, 1, 8, 0, 0, TimeSpan.Zero);
        return
        [
            Q("equals scalar", p => p.LastName == "Smith", p => p.FirstName),
            Q("null check", p => p.Email == null),
            Q("ids.Contains(Id) (In)", p => ids.Contains(p.Id!)),
            Q("id prefix (StartsWith)", p => p.Id!.StartsWith("people/")),
            Q("nested member", p => p.Address != null && p.Address.City == "Ghent"),
            Q("enum equals", p => p.Status == Status.Retired),
            Q("DateOnly compare", p => p.DateOfBirth > new DateOnly(1970, 1, 1)),
            Q("DateTimeOffset compare", p => p.Hired > hiredAfter),
            Q("string Contains (search)", p => p.FirstName.Contains("le") || p.LastName.Contains("le")),
            Q("AnyContains primitive list", p => p.Professions != null && p.Professions.Any(x => x == "professions/2")),
            Q("primitive Contains", p => p.Professions.Contains("professions/3")),
            Q("Any over complex collection", p => p.Jobs.Any(j => j.ProfessionId == "professions/2")),
            Q("captured array Any", p => p.Professions.Any(x => wanted.Contains(x))),
            Q("computed get-only FullName", p => p.FullName == "Ann Smith"),
            Q("order by nested", p => true, p => p.Address!.City),
        ];
    }
}
