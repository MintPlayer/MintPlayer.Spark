// Round 3: the hybrid. EF Core only TRANSLATES queries (complex JSON model over the Data column); Newtonsoft
// writes and reads the document (raw string over the same column); writes are our own parameterized SQL.
using System.Linq.Expressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Newtonsoft.Json;

public static class Round3
{
    public static async Task RunAsync(string cs, int compat)
    {
        Console.WriteLine("\n######## ROUND 3 (hybrid)");
        await using var db = new R3Db(cs, compat);
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();
        Console.WriteLine(db.Database.GenerateCreateScript().Split("GO")[0].Trim());

        // Writes: Newtonsoft JSON (get-only FullName included, [JsonConverter]s honoured), own SQL.
        await using (var conn = new SqlConnection(cs))
        {
            await conn.OpenAsync();
            foreach (var p in Seed.People())
                await Insert(conn, p.Id!, "People", JsonConvert.SerializeObject(p, J.Settings));
            // A document from an older model: no LastName, no Status, no Jobs, plus an undeclared field.
            await Insert(conn, "people/9", "People", """{"FirstName":"Old","Legacy":"keep me"}""");
        }

        var queries = Seed.Queries();
        queries.Add(("absent Status == default", p => p.Status == Status.Active, null));
        queries.Add(("absent Status == default (D16 form)", p => p.Status == Status.Active || SqlJson.Value(p.Id!, "$.Status") == null, null));
        queries.Add(("absent Jobs: !Any() ", p => !p.Jobs.Any(), null));
        foreach (var (name, predicate, order) in queries)
        {
            try
            {
                var docs = db.Set<Doc<Person>>().AsNoTracking()
                    .Where(d => d.Collection == "People")
                    .Where(R3Rewrite.OntoData(predicate));
                if (order is not null) docs = docs.OrderBy(R3Rewrite.OntoData(order)).Skip(0).Take(10);
                var raw = docs.Select(d => EF.Property<string>(d, "Raw"));
                var sql = raw.ToQueryString();
                var rows = (await raw.ToListAsync()).Select(j => JsonConvert.DeserializeObject<Person>(j, J.Settings)!).ToList();
                Console.WriteLine($"OK   {name,-36} rows={rows.Count} [{string.Join(",", rows.Select(r => r.FirstName))}]");
                if (name.Contains("FullName") || name.Contains("absent") || name.Contains("nested") || name.Contains("enum"))
                    Console.WriteLine("     " + sql.Replace("\r", "").Replace("\n", " "));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAIL {name,-36} {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
            }
        }
    }

    static async Task Insert(SqlConnection conn, string id, string collection, string json)
    {
        await using var cmd = new SqlCommand("INSERT INTO Docs (Id, Collection, Data) VALUES (@id, @c, @d)", conn);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@c", collection);
        cmd.Parameters.AddWithValue("@d", json);
        await cmd.ExecuteNonQueryAsync();
    }
}

public class R3Db(string cs, int compat) : DbContext
{
    protected override void OnConfiguring(DbContextOptionsBuilder o)
        => o.UseSqlServer(cs, s => s.UseCompatibilityLevel(compat))
            .ReplaceService<IModelCacheKeyFactory, Key>();

    private sealed class Key : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) => ("r3", designTime);
    }

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<Doc<Person>>(b =>
        {
            b.ToTable("Docs");
            b.HasKey(d => d.Id);
            b.Property<byte[]>("Version").IsRowVersion();
            b.Property<string>("Raw").HasComputedColumnSql(compat >= 170 ? "CAST([Data] AS nvarchar(max))" : "[Data]");
            b.ComplexProperty(d => d.Data, data =>
            {
                data.ToJson("Data");
                data.ComplexProperty(p => p.Address);
                data.ComplexCollection(p => p.Jobs);
                data.Property(p => p.Status).HasConversion<string>();
            });
        });
        mb.HasDbFunction(typeof(SqlJson).GetMethod(nameof(SqlJson.Value))!).HasName("JSON_VALUE").IsBuiltIn();
    }
}

public static class SqlJson
{
    /// <summary>JSON_VALUE(json, path): a get-only (persisted, computed) property has no EF mapping, so it is read by path.</summary>
    public static string? Value(string json, string path) => throw new NotSupportedException("SQL only");
}

public static class R3Rewrite
{
    /// <summary>
    /// x => body(x) on T  ==>  d => body(d.Data) on Doc&lt;T&gt;. A get-only property becomes EF.Property on the
    /// complex Data (its shadow twin).
    /// </summary>
    public static Expression<Func<Doc<T>, TResult>> OntoData<T, TResult>(Expression<Func<T, TResult>> lambda) where T : class
    {
        var d = Expression.Parameter(typeof(Doc<T>), "d");
        var body = new Swap(lambda.Parameters[0], Expression.Property(d, nameof(Doc<T>.Data))).Visit(lambda.Body);
        return Expression.Lambda<Func<Doc<T>, TResult>>(body, d);
    }

    private sealed class Swap(ParameterExpression from, Expression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;

        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Expression == from && node.Member is System.Reflection.PropertyInfo { CanWrite: false, PropertyType: var t } prop && t == typeof(string))
            {
                var doc = ((MemberExpression)to).Expression!;
                var raw = Expression.Call(typeof(EF).GetMethod(nameof(EF.Property))!.MakeGenericMethod(typeof(string)), doc, Expression.Constant("Raw"));
                return Expression.Call(typeof(SqlJson).GetMethod(nameof(SqlJson.Value))!, raw, Expression.Constant("$." + prop.Name));
            }
            return base.VisitMember(node);
        }

        // The D16 marker SqlJson.Value(x.Id, path) in a T-lambda: its first argument becomes the raw document.
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.DeclaringType == typeof(SqlJson) && node.Arguments[0] is MemberExpression m && m.Expression == from)
            {
                var doc = ((MemberExpression)to).Expression!;
                var raw = Expression.Call(typeof(EF).GetMethod(nameof(EF.Property))!.MakeGenericMethod(typeof(string)), doc, Expression.Constant("Raw"));
                return Expression.Call(node.Method, raw, node.Arguments[1]);
            }
            return base.VisitMethodCall(node);
        }
    }
}

public static class J
{
    // Copy of InMemoryJson.Settings (minus the Color converter): the RavenDB client's conventions.
    public static readonly Newtonsoft.Json.JsonSerializerSettings Settings = new()
    {
        Converters = { new Newtonsoft.Json.Converters.StringEnumConverter() },
        ObjectCreationHandling = Newtonsoft.Json.ObjectCreationHandling.Replace,
        NullValueHandling = Newtonsoft.Json.NullValueHandling.Include,
        DateParseHandling = Newtonsoft.Json.DateParseHandling.None,
    };
}
