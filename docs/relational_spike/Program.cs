// #536 spike S12/S13: does the D1 hybrid (Newtonsoft owns the document, EF Core 11 only translates queries over a
// complex ToJson model of Data) carry over to PostgreSQL (Npgsql 11) and MariaDB (Microting 11, Pomelo fork)?
// Same model, seed and query shapes as docs/sql_server_spike (round 3).
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Newtonsoft.Json;

var provider = args.Length > 0 ? args[0] : "pg";
Console.WriteLine($"== provider {provider}");
Rewrite.Provider = provider;
await using var db = new HybridDb(provider);
await db.Database.EnsureDeletedAsync();
await db.Database.EnsureCreatedAsync();
Console.WriteLine(db.Database.GenerateCreateScript().Trim());

foreach (var p in Seed.People())
    await Insert(db, p.Id!, "People", JsonConvert.SerializeObject(p, J.Settings));
await Insert(db, "people/9", "People", """{"FirstName":"Old","Legacy":"keep me"}""");

var queries = Seed.Queries();
queries.Add(("absent Status == default", p => p.Status == Status.Active, null));
queries.Add(("absent Status == default (D16 form)", p => p.Status == Status.Active || DocJson.Value(p.Id!, "Status") == null, null));
queries.Add(("absent Jobs: !Any()", p => !p.Jobs.Any(), null));
foreach (var (name, predicate, order) in queries)
{
    try
    {
        var docs = db.Set<Doc<Person>>().AsNoTracking()
            .Where(d => d.Collection == "People")
            .Where(Rewrite.OntoData(predicate));
        if (order is not null) docs = docs.OrderBy(Rewrite.OntoData(order)).Skip(0).Take(10);
        var raw = docs.Select(d => EF.Property<string>(d, "Raw"));
        var sql = raw.ToQueryString();
        var rows = (await raw.ToListAsync()).Select(j => JsonConvert.DeserializeObject<Person>(j, J.Settings)!).ToList();
        Console.WriteLine($"OK   {name,-36} rows={rows.Count} [{string.Join(",", rows.Select(r => r.FirstName))}]");
        var where = sql.Replace("\r", "").Replace("\n", " ");
        var i = where.IndexOf("WHERE", StringComparison.Ordinal);
        Console.WriteLine("     " + (i >= 0 ? where[i..] : where));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL {name,-36} {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
    }
}

// Round trip: does the stored text come back byte-identical to what Newtonsoft wrote?
var written = JsonConvert.SerializeObject(Seed.People()[0], J.Settings);
var back = await db.Set<Doc<Person>>().AsNoTracking().Where(d => d.Id == "people/1").Select(d => EF.Property<string>(d, "Raw")).SingleAsync();
Console.WriteLine($"round trip identical text: {written == back}");
if (written != back) Console.WriteLine($"     wrote {written}\n     read  {back}");

// Optimistic concurrency with our own SQL (no rowversion on PG/MariaDB): Version bigint, UPDATE ... WHERE Version = @v.
var first = await db.Database.ExecuteSqlAsync(FormattableStringFactory(VersionSql(provider), "people/1", 0L));
var second = await db.Database.ExecuteSqlAsync(FormattableStringFactory(VersionSql(provider), "people/1", 0L));
Console.WriteLine($"concurrency: first update rows={first}, stale update rows={second} (want 1, 0)");

static FormattableString FormattableStringFactory(string format, params object[] a) => System.Runtime.CompilerServices.FormattableStringFactory.Create(format, a);
static string VersionSql(string provider) => provider == "pg"
    ? "UPDATE \"Docs\" SET \"Version\" = \"Version\" + 1 WHERE \"Id\" = {0} AND \"Version\" = {1}"
    : "UPDATE Docs SET Version = Version + 1 WHERE Id = {0} AND Version = {1}";

static async Task Insert(HybridDb db, string id, string collection, string json)
{
    if (db.Provider == "pg")
        await db.Database.ExecuteSqlAsync($"INSERT INTO \"Docs\" (\"Id\", \"Collection\", \"Data\", \"Version\") VALUES ({id}, {collection}, CAST({json} AS jsonb), 0)");
    else
        await db.Database.ExecuteSqlAsync($"INSERT INTO Docs (Id, Collection, Data, Version) VALUES ({id}, {collection}, {json}, 0)");
}

// ---------- model (copy of docs/sql_server_spike/Program.cs) ----------
public class Person
{
    public string? Id { get; set; }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string FullName => $"{FirstName} {LastName}";
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
public class Doc<T> where T : class { public string Id { get; set; } = ""; public string Collection { get; set; } = ""; public T Data { get; set; } = default!; }

public class HybridDb(string provider) : DbContext
{
    public string Provider => provider;

    protected override void OnConfiguring(DbContextOptionsBuilder o)
    {
        if (provider == "pg")
            o.UseNpgsql("Host=127.0.0.1;Port=55432;Username=postgres;Password=spike536;Database=sparkspike536", n => n.SetPostgresVersion(18, 0));
        else
            o.UseMySql("Server=127.0.0.1;Port=53306;User=root;Password=spike536;Database=sparkspike536",
                new MariaDbServerVersion(new Version(11, 8)), m => m.EnablePrimitiveCollectionsSupport());
        o.ReplaceService<IModelCacheKeyFactory, Key>();
    }

    private sealed class Key : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) => (((HybridDb)context).Provider, designTime);
    }

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<Doc<Person>>(b =>
        {
            b.ToTable("Docs");
            b.HasKey(d => d.Id);
            b.Property<long>("Version");
            // Raw: a non-persisted alias of Data, read by Newtonsoft (D1 / §8.1 finding 2).
            if (provider == "pg")
                b.Property<string>("Raw").HasColumnType("jsonb").HasComputedColumnSql("\"Data\"", stored: false);
            else
                b.Property<string>("Raw").HasColumnType("longtext").HasComputedColumnSql("`Data`", stored: false);
            b.ComplexProperty(d => d.Data, data =>
            {
                data.ToJson("Data");
                data.ComplexProperty(p => p.Address);
                data.ComplexCollection(p => p.Jobs);
                data.Property(p => p.Status).HasConversion<string>();
            });
        });
        var fn = mb.HasDbFunction(typeof(DocJson).GetMethod(nameof(DocJson.Value))!).IsBuiltIn();
        fn.HasName(provider == "pg" ? "jsonb_extract_path_text" : "JSON_VALUE");
    }
}

public static class DocJson
{
    /// <summary>Read a top-level member by name from the raw document (get-only members, D16 absent checks).</summary>
    public static string? Value(string json, string member) => throw new NotSupportedException("SQL only");
}

public static class Rewrite
{
    public static string Provider = "pg";

    public static Expression<Func<Doc<T>, TResult>> OntoData<T, TResult>(Expression<Func<T, TResult>> lambda) where T : class
    {
        var d = Expression.Parameter(typeof(Doc<T>), "d");
        var body = new Swap(lambda.Parameters[0], Expression.Property(d, nameof(Doc<T>.Data))).Visit(lambda.Body);
        return Expression.Lambda<Func<Doc<T>, TResult>>(body, d);
    }

    static Expression Path(string member) => Expression.Constant(Provider == "pg" ? member : "$." + member);

    private sealed class Swap(ParameterExpression from, Expression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;

        Expression Raw() => Expression.Call(typeof(EF).GetMethod(nameof(EF.Property))!.MakeGenericMethod(typeof(string)),
            ((MemberExpression)to).Expression!, Expression.Constant("Raw"));

        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Expression == from && node.Member is System.Reflection.PropertyInfo { CanWrite: false, PropertyType: var t } prop && t == typeof(string))
                return Expression.Call(typeof(DocJson).GetMethod(nameof(DocJson.Value))!, Raw(), Path(prop.Name));
            return base.VisitMember(node);
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.DeclaringType == typeof(DocJson) && node.Arguments[0] is MemberExpression m && m.Expression == from)
                return Expression.Call(node.Method, Raw(), Path((string)((ConstantExpression)node.Arguments[1]).Value!));
            return base.VisitMethodCall(node);
        }
    }
}

public static class J
{
    public static readonly JsonSerializerSettings Settings = new()
    {
        Converters = { new Newtonsoft.Json.Converters.StringEnumConverter() },
        ObjectCreationHandling = ObjectCreationHandling.Replace,
        NullValueHandling = NullValueHandling.Include,
        DateParseHandling = DateParseHandling.None,
    };
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
