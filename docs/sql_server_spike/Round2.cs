// Round 2: serialization questions for the C2 document mapping.
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

public static class Round2
{
    public static async Task RunAsync(string cs, int compat)
    {
        Console.WriteLine("\n######## ROUND 2");

        // (a) can a get-only computed property be mapped inside the JSON?
        await Try("a: map get-only FullName", async () =>
        {
            await using var db = new R2Db(cs, compat, "a");
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            db.Add(new Doc<Person> { Id = "people/1", Collection = "People", Data = new() { FirstName = "Ann", LastName = "Smith" } });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var n = await db.Set<Doc<Person>>().CountAsync(d => d.Data.FullName == "Ann Smith");
            return $"rows={n}";
        });

        // (e) one column read two ways: the complex JSON for translation, a raw string for Newtonsoft
        await Try("e: shadow string on same column", async () =>
        {
            await using var db = new R2Db(cs, compat, "e");
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            return "model built";
        });

        // (b) absent fields (D16) and (c) unknown fields: a document written by someone else
        await Try("b/c: absent + unknown fields", async () =>
        {
            await using var db = new R2Db(cs, compat, "b");
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Docs (Id, Collection, Data) VALUES ('people/9', 'People', '{\"FirstName\":\"Old\",\"Legacy\":\"keep me\"}')");
            var doc = await db.Set<Doc<Person>>().SingleAsync(d => d.Id == "people/9");
            var read = $"read OK: Last='{doc.Data.LastName}' Status={doc.Data.Status} Jobs={doc.Data.Jobs?.Count.ToString() ?? "null"} Professions={doc.Data.Professions?.Count.ToString() ?? "null"}";
            var countAbsent = await db.Set<Doc<Person>>().CountAsync(d => d.Data.Status == Status.Active);
            doc.Data.Email = "x@example.org";
            await db.SaveChangesAsync();
            var json = await db.Database.SqlQueryRaw<string>("SELECT CAST(Data AS nvarchar(max)) AS Value FROM Docs WHERE Id='people/9'").SingleAsync();
            return $"{read}; Status==Active matches absent: {countAbsent}; after update: {json}";
        });
    }

    static async Task Try(string name, Func<Task<string>> f)
    {
        try { Console.WriteLine($"OK   {name}: {await f()}"); }
        catch (Exception ex) { Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}"); }
    }
}

public class R2Db(string cs, int compat, string variant) : DbContext
{
    public string Variant => variant;
    protected override void OnConfiguring(DbContextOptionsBuilder o)
        => o.UseSqlServer(cs, s => s.UseCompatibilityLevel(compat))
            .ReplaceService<IModelCacheKeyFactory, Key>();

    private sealed class Key : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) => ("r2", ((R2Db)context).Variant, designTime);
    }

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<Doc<Person>>(b =>
        {
            b.ToTable("Docs");
            b.HasKey(d => d.Id);
            b.Property<byte[]>("Version").IsRowVersion();
            if (variant == "e")
                b.Property<string>("Raw").HasColumnName("Data");
            b.ComplexProperty(d => d.Data, data =>
            {
                data.ToJson("Data");
                data.ComplexProperty(p => p.Address);
                data.ComplexCollection(p => p.Jobs);
                data.Property(p => p.Status).HasConversion<string>();
                if (variant == "a")
                    data.Property(p => p.FullName);
            });
        });
    }
}
