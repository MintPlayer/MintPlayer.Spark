// #536 spike S2b: an EF Core prototype of Spark's storage seam (ISparkStore, ISparkSession, ISparkQueryProvider), so
// the real QueryExecutor runs unmodified over relational tables (PRD D1, D25, D26). Throwaway: it covers what the
// executor's read path needs, plus the writes the seed uses. Versions (S4) and views (S5) are not here.
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Query;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Storage;

namespace S2b;

/// <summary>What the model synchronize would derive from the Spark model (D24): the mapping of each collection.</summary>
public sealed class EfSparkModel
{
    /// <summary>Root entity types (one table each, TPH for derived types) and their collection (table) names.</summary>
    public List<(Type Root, string Table, Type[] Derived)> Collections { get; } = [];

    /// <summary>A <c>List&lt;string&gt;</c> of reference ids stored in a framework-owned join table (D26).</summary>
    public List<JoinCollection> Joins { get; } = [];

    /// <summary>App-specific mapping a Spark model would also carry (unique attributes, lengths).</summary>
    public Action<ModelBuilder>? Configure { get; set; }

    public string CollectionOf(Type type)
        => Collections.FirstOrDefault(c => c.Root == type || c.Derived.Contains(type) || c.Root.IsAssignableFrom(type)).Table
           ?? throw new InvalidOperationException($"{type.Name} is not a mapped collection.");

    /// <summary>
    /// The root type an untyped id belongs to, by its lowercase collection prefix. This is what D27 removes: the typed
    /// contract passes the type, so a provider never parses an id.
    /// </summary>
    public Type RootOfId(string id)
        => Collections.FirstOrDefault(c => id.StartsWith(c.Table.ToLowerInvariant() + "/", StringComparison.Ordinal)).Root
           ?? throw new InvalidOperationException($"No collection for id '{id}'.");
}

public sealed record JoinCollection(Type Owner, string Property, Type Target, string Table)
{
    public PropertyInfo PropertyInfo => Owner.GetProperty(Property)!;
}

/// <summary>The DbContext Spark builds from its model: no DbSet properties, no attributes on the entities.</summary>
public sealed class SparkEfContext(DbContextOptions options, EfSparkModel model) : DbContext(options)
{
    public EfSparkModel SparkModel => model;

    protected override void OnModelCreating(ModelBuilder mb)
    {
        foreach (var (root, table, derived) in model.Collections)
        {
            var eb = mb.Entity(root);
            eb.ToTable(table);
            eb.HasKey("Id");
            eb.Property("Id").HasMaxLength(200).ValueGeneratedNever().UseCollation(KeyCollation);
            // D3: the version, a bigint concurrency token the session increments (never the database).
            eb.Property<long>("Version").IsConcurrencyToken();
            foreach (var d in derived) mb.Entity(d).HasBaseType(root); // TPH: one table, a discriminator column
            foreach (var type in derived.Prepend(root)) MapMembers(mb, mb.Entity(type), type);
        }

        foreach (var j in model.Joins)
        {
            // D26: a framework-owned join entity; the CLR list is ignored by EF and filled by the session.
            mb.SharedTypeEntity<Dictionary<string, object>>(j.Table, b =>
            {
                b.IndexerProperty<string>("OwnerId").HasMaxLength(200).UseCollation(KeyCollation);
                b.IndexerProperty<string>("TargetId").HasMaxLength(200).UseCollation(KeyCollation);
                b.HasKey("OwnerId", "TargetId");
                b.HasOne(j.Owner).WithMany().HasForeignKey("OwnerId").OnDelete(DeleteBehavior.Cascade);
                b.HasOne(j.Target).WithMany().HasForeignKey("TargetId").OnDelete(DeleteBehavior.Restrict);
                b.HasIndex("TargetId");
            });
        }

        model.Configure?.Invoke(mb);
    }

    /// <summary>D7: key and FK columns compare bytes; ids are lowercase by construction.</summary>
    private string KeyCollation => Database.IsNpgsql() ? "C" : "Latin1_General_BIN2";

    private void MapMembers(ModelBuilder mb, EntityTypeBuilder eb, Type type)
    {
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (p.Name == "Id") continue;
            if (!p.CanWrite)
            {
                // O13: a stored get-only property. The generator emits a backing field EF writes on materialization;
                // saves read the getter, so the dirty check sees a recomputed value and rewrites the column.
                if (type.GetField("__stored_" + p.Name, BindingFlags.NonPublic | BindingFlags.Instance) is null) continue;
                eb.Property(p.Name).HasField("__stored_" + p.Name).UsePropertyAccessMode(PropertyAccessMode.PreferFieldDuringConstruction);
                continue;
            }
            if (model.Joins.Any(j => j.Owner == type && j.Property == p.Name)) { eb.Ignore(p.Name); continue; }

            var reference = p.GetCustomAttribute<ReferenceAttribute>();
            if (reference is not null && p.PropertyType == typeof(string))
            {
                // D26: a real FK, no navigation property.
                eb.Property(p.Name).HasMaxLength(200).UseCollation(KeyCollation);
                eb.HasOne(reference.TargetType).WithMany().HasForeignKey(p.Name).OnDelete(DeleteBehavior.Restrict);
                continue;
            }

            if (p.PropertyType == typeof(TranslatedString))
            {
                // D1: a core value type without a relational shape is a JSON column, written by its own converter.
                eb.Property(p.Name).HasConversion(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<TranslatedString?, string?>(
                    v => v == null ? null : JsonSerializer.Serialize(v, JsonSerializerOptions.Default),
                    v => v == null ? null : JsonSerializer.Deserialize<TranslatedString>(v, JsonSerializerOptions.Default)));
                if (Database.IsNpgsql()) eb.Property(p.Name).HasColumnType("jsonb");
                continue;
            }

            var element = p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(List<>)
                ? p.PropertyType.GetGenericArguments()[0] : null;
            if (element is not null && element.IsClass && element != typeof(string) && element.GetProperty("Id") is not null)
            {
                // D25: an AsDetail collection is a child table keyed by (owner id, child id), cascade delete.
                var ownerType = type;
                eb.OwnsMany(element, p.Name, ob =>
                {
                    ob.ToTable(ownerType.Name + p.Name);
                    ob.WithOwner().HasForeignKey("OwnerId");
                    ob.Property<string>("OwnerId").HasMaxLength(200).UseCollation(KeyCollation);
                    ob.Property("Id").HasMaxLength(200);
                    ob.HasKey("OwnerId", "Id");
                });
            }
        }
    }
}

/// <summary>The store: options per provider, one DbContext per session.</summary>
public sealed class EfSparkStore(DbContextOptions options, EfSparkModel model, string providerName) : ISparkStore
{
    public string ProviderName => providerName;
    public SparkStoreCapabilities Capabilities => SparkStoreCapabilities.None;
    public EfSparkModel Model => model;
    public SparkEfContext NewContext() => new(options, model);
    public ISparkSession OpenSession() => new EfSparkSession(NewContext());
    public string GetCollectionName(Type type) => model.CollectionOf(type);

    public Task<bool> WaitForConsistencyAsync(ISparkSession? session, IReadOnlyCollection<string> collections, TimeSpan bound,
        Action<Exception, string, TimeSpan>? onUnfinished, CancellationToken cancellationToken) => Task.FromResult(true);
}

public sealed class EfSparkSession(SparkEfContext db) : ISparkSession
{
    public SparkEfContext Db => db;
    public object Native => db;
    public int NumberOfRequests { get; private set; }
    public int MaxNumberOfRequests { get; set; } = int.MaxValue;
    public event Action<object>? BeforeStore;
    public event Action<object>? BeforeDelete;

    public IQueryable<T> Query<T>() where T : class => db.Set<T>();
    public IQueryable<T> Query<T, TIndex>() where T : class where TIndex : class, new()
        => throw new NotSupportedException("Views are spike S5.");

    // ---- loads ----
    public async Task<T?> LoadAsync<T>(string id, CancellationToken cancellationToken = default) where T : class
        => (await LoadAsync<T>([id], cancellationToken))[id];

    public async Task<IReadOnlyDictionary<string, T?>> LoadAsync<T>(IEnumerable<string> ids, CancellationToken cancellationToken = default) where T : class
    {
        var wanted = ids.Distinct().ToList();
        NumberOfRequests++;
        var rows = await db.Set<T>().Where(x => wanted.Contains(EF.Property<string>(x, "Id"))).ToListAsync(cancellationToken);
        await HydrateAsync(rows, cancellationToken);
        var byId = rows.ToDictionary(r => GetId(r)!, StringComparer.Ordinal);
        return wanted.ToDictionary(id => id, id => byId.GetValueOrDefault(id));
    }

    public Task<IReadOnlyDictionary<string, T?>> LoadAsync<T>(IEnumerable<string> ids, IReadOnlyCollection<string> includePaths, CancellationToken cancellationToken = default) where T : class
        => LoadAsync<T>(ids, cancellationToken);

    public Task PreloadAsync(IReadOnlyCollection<(Type EntityType, string Id)> documents, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task<object?> LoadAsync(Type entityType, string id, CancellationToken cancellationToken = default)
        => (await LoadAsync(entityType, [id], cancellationToken))[id];

    public async Task<IReadOnlyDictionary<string, object?>> LoadAsync(Type entityType, IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        var method = typeof(EfSparkSession).GetMethods().Single(m => m.Name == nameof(LoadAsync) && m.IsGenericMethod
            && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == typeof(IEnumerable<string>)).MakeGenericMethod(entityType);
        var task = (Task)method.Invoke(this, [ids, cancellationToken])!;
        await task;
        var dict = (System.Collections.IDictionary)task.GetType().GetProperty("Result")!.GetValue(task)!;
        return dict.Keys.Cast<string>().ToDictionary(k => k, k => dict[k]);
    }

    public async Task<IReadOnlyList<T>> LoadStartingWithAsync<T>(string idPrefix, int start = 0, int pageSize = 25, CancellationToken cancellationToken = default) where T : class
    {
        NumberOfRequests++;
        // D7: StartsWith translates to LIKE with the prefix's wildcards escaped (S4 checks _ and %).
        var rows = await db.Set<T>().Where(x => EF.Property<string>(x, "Id").StartsWith(idPrefix))
            .OrderBy(x => EF.Property<string>(x, "Id")).Skip(start).Take(pageSize).ToListAsync(cancellationToken);
        await HydrateAsync(rows, cancellationToken);
        return rows;
    }

    public async Task<bool> ExistsAsync(string id, CancellationToken cancellationToken = default)
    {
        NumberOfRequests++;
        return (await VersionsOfAsync(db.SparkModel.RootOfId(id), [id], cancellationToken)).ContainsKey(id);
    }

    // ---- writes ----
    /// <summary>Entities stored with an expected version of "": the commit fails if their row exists (D3).</summary>
    private readonly HashSet<object> mustNotExist = new(ReferenceEqualityComparer.Instance);

    public Task StoreAsync(object entity, CancellationToken cancellationToken = default)
    {
        var idProp = entity.GetType().GetProperty("Id")!;
        if (idProp.GetValue(entity) is not string { Length: > 0 })
            idProp.SetValue(entity, $"{db.SparkModel.CollectionOf(entity.GetType()).ToLowerInvariant()}/{Guid.NewGuid():N}");
        if (db.Entry(entity).State == EntityState.Detached) db.Add(entity);
        return Task.CompletedTask;
    }

    public Task StoreAsync(object entity, string id, CancellationToken cancellationToken = default)
    {
        entity.GetType().GetProperty("Id")!.SetValue(entity, id);
        return StoreAsync(entity, cancellationToken);
    }

    /// <summary>
    /// D3: "" inserts (a duplicate key is a concurrency failure); a version makes the UPDATE/DELETE carry
    /// <c>WHERE Version = @expected</c>; null checks nothing.
    /// </summary>
    public Task StoreAsync(object entity, string? expectedVersion, string id, CancellationToken cancellationToken = default)
    {
        entity.GetType().GetProperty("Id")!.SetValue(entity, id);
        var entry = db.Entry(entity);
        switch (expectedVersion)
        {
            case null:
                return StoreAsync(entity, cancellationToken);
            case "":
                mustNotExist.Add(entity);
                if (entry.State == EntityState.Detached) db.Add(entity);
                return Task.CompletedTask;
            default:
                if (entry.State == EntityState.Detached) db.Update(entity); // every column, as a document PUT
                entry.Property("Version").OriginalValue = ParseVersion(expectedVersion);
                return Task.CompletedTask;
        }
    }

    public void Delete(string id, string? expectedVersion = null)
    {
        var root = db.SparkModel.RootOfId(id);
        var tracked = db.ChangeTracker.Entries().FirstOrDefault(e => !e.Metadata.IsOwned() && root.IsInstanceOfType(e.Entity) && GetId(e.Entity) == id);
        object entity;
        if (tracked is not null) entity = tracked.Entity;
        else
        {
            // A key-only stub: DELETE … WHERE Id = @id [AND Version = @v]. Child and join rows go by ON DELETE CASCADE.
            // Without a version the commit reads the current one first (an unconditional delete).
            entity = Activator.CreateInstance(root)!;
            root.GetProperty("Id")!.SetValue(entity, id);
            db.Attach(entity);
        }
        var entry = db.Entry(entity);
        if (expectedVersion is not null) entry.Property("Version").OriginalValue = ParseVersion(expectedVersion);
        else if (tracked is null) unversionedDeletes.Add(entity);
        Delete(entity);
    }

    private readonly HashSet<object> unversionedDeletes = new(ReferenceEqualityComparer.Instance);

    public void Delete(object entity) { BeforeDelete?.Invoke(entity); db.Remove(entity); }

    private static long ParseVersion(string version)
        => long.TryParse(version, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v : throw new SparkStoreConcurrencyException("The version is not one this store issued.");

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        foreach (var e in db.ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified && !e.Metadata.IsOwned()).ToList())
            BeforeStore?.Invoke(e.Entity);

        foreach (var entity in mustNotExist)
            if (db.Entry(entity).State != EntityState.Added)
                throw new SparkStoreConcurrencyException($"'{GetId(entity)}' was expected not to exist.");

        foreach (var group in unversionedDeletes.GroupBy(e => db.SparkModel.RootOfId(GetId(e)!)))
        {
            var current = await VersionsOfAsync(group.Key, [.. group.Select(e => GetId(e)!)], cancellationToken);
            foreach (var e in group)
            {
                if (current.TryGetValue(GetId(e)!, out var v)) db.Entry(e).Property("Version").OriginalValue = v;
                else db.Entry(e).State = EntityState.Detached; // already gone: nothing to delete, as RavenDB
            }
        }

        // Join rows (D26): rewrite an owner's rows when its CLR list differs from what is stored.
        var owners = db.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Unchanged && !e.Metadata.IsOwned())
            .Select(e => e.Entity).ToList();
        var changedOwners = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var j in db.SparkModel.Joins)
        {
            foreach (var owner in owners.Where(o => j.Owner.IsInstanceOfType(o)))
            {
                var ownerId = GetId(owner)!;
                var set = db.Set<Dictionary<string, object>>(j.Table);
                var existing = await set.Where(r => EF.Property<string>(r, "OwnerId") == ownerId).ToListAsync(cancellationToken);
                var wanted = ((IEnumerable<string>?)j.PropertyInfo.GetValue(owner) ?? []).Distinct().ToHashSet(StringComparer.Ordinal);
                if (wanted.SetEquals(existing.Select(r => (string)r["TargetId"]))) continue;
                set.RemoveRange(existing);
                foreach (var target in wanted)
                    set.Add(new Dictionary<string, object> { ["OwnerId"] = ownerId, ["TargetId"] = target });
                changedOwners.Add(owner);
            }
        }

        // D3: the aggregate's version moves when its row, a child row (D25) or a join row (D26) changes.
        foreach (var e in db.ChangeTracker.Entries().Where(e => e.Metadata.IsOwned() && e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList())
        {
            var ownerId = (string?)(e.Property("OwnerId").CurrentValue ?? e.Property("OwnerId").OriginalValue);
            var owner = db.ChangeTracker.Entries().FirstOrDefault(o => !o.Metadata.IsOwned() && o.Metadata.FindProperty("Version") is not null && GetId(o.Entity) == ownerId);
            if (owner is not null) changedOwners.Add(owner.Entity);
        }
        foreach (var e in db.ChangeTracker.Entries().Where(e => !e.Metadata.IsOwned() && e.Metadata.FindProperty("Version") is not null).ToList())
        {
            var version = e.Property("Version");
            if (e.State == EntityState.Added) version.CurrentValue = 1L;
            else if (e.State == EntityState.Modified || (e.State == EntityState.Unchanged && changedOwners.Contains(e.Entity)))
                version.CurrentValue = (long)version.OriginalValue! + 1;
        }

        NumberOfRequests++;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new SparkStoreConcurrencyException("The row changed or was deleted since it was read.", ex);
        }
        catch (DbUpdateException ex)
        {
            throw ConstraintErrors.Translate(db, ex);
        }
        await tx.CommitAsync(cancellationToken);
        mustNotExist.Clear();
        unversionedDeletes.Clear();
    }

    // ---- identity map ----
    public string? GetId(object entity) => entity.GetType().GetProperty("Id")?.GetValue(entity) as string;

    public string? GetVersion(object entity)
    {
        var entry = db.Entry(entity);
        if (entry.State == EntityState.Detached) throw new InvalidOperationException("The session does not track this entity.");
        return ((long)entry.Property("Version").CurrentValue!).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Untyped, so the ids are grouped by their collection prefix (D27 passes the type instead).</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetVersionsAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in ids.Distinct().GroupBy(db.SparkModel.RootOfId))
        {
            NumberOfRequests++;
            foreach (var (id, v) in await VersionsOfAsync(group.Key, [.. group], cancellationToken))
                result[id] = v.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return result;
    }

    private Task<Dictionary<string, long>> VersionsOfAsync(Type root, List<string> ids, CancellationToken cancellationToken)
        => (Task<Dictionary<string, long>>)typeof(EfSparkSession).GetMethod(nameof(VersionsOfTypedAsync), BindingFlags.NonPublic | BindingFlags.Instance)!
            .MakeGenericMethod(root).Invoke(this, [ids, cancellationToken])!;

    private Task<Dictionary<string, long>> VersionsOfTypedAsync<T>(List<string> ids, CancellationToken cancellationToken) where T : class
        => db.Set<T>().AsNoTracking().Where(x => ids.Contains(EF.Property<string>(x, "Id")))
            .Select(x => new { Id = EF.Property<string>(x, "Id"), Version = EF.Property<long>(x, "Version") })
            .ToDictionaryAsync(x => x.Id, x => x.Version, StringComparer.Ordinal, cancellationToken);
    public string GetCollectionName(Type type) => db.SparkModel.CollectionOf(type);
    public string? GetStoredCollectionName(object entity) => db.SparkModel.CollectionOf(entity.GetType());
    public bool IsLoaded(string id) => db.ChangeTracker.Entries().Any(e => GetId(e.Entity) == id);
    public bool HasChanged(object entity) => db.Entry(entity).State != EntityState.Unchanged;
    public IReadOnlyCollection<SparkTrackedEntity> TrackedEntities
        => [.. db.ChangeTracker.Entries().Where(e => e.Metadata.ClrType != typeof(Dictionary<string, object>) && !e.Metadata.IsOwned())
            .Select(e => new SparkTrackedEntity(e.Entity, e.State == EntityState.Deleted))];
    /// <summary>Detaches the aggregate: the row and its tracked child rows.</summary>
    public void Evict(object entity)
    {
        var id = GetId(entity);
        foreach (var owned in db.ChangeTracker.Entries().Where(e => e.Metadata.IsOwned() && (string?)e.Property("OwnerId").CurrentValue == id).ToList())
            owned.State = EntityState.Detached;
        db.Entry(entity).State = EntityState.Detached;
    }
    public void Clear() => db.ChangeTracker.Clear();

    // ---- execution: every query goes through the join-table rewrite, then fills the join lists ----
    public async Task<List<T>> ToListAsync<T>(IQueryable<T> query, CancellationToken cancellationToken = default)
    {
        NumberOfRequests++;
        var rows = await JoinRewriter.Rewrite(db, query).ToListAsync(cancellationToken);
        await HydrateAsync(rows.Cast<object>(), cancellationToken);
        return rows;
    }

    public Task<int> CountAsync<T>(IQueryable<T> query, CancellationToken cancellationToken = default)
    {
        NumberOfRequests++;
        return JoinRewriter.Rewrite(db, query).CountAsync(cancellationToken);
    }

    /// <summary>Fills the join-table lists of materialized owners: one query per join collection.</summary>
    public async Task HydrateAsync(IEnumerable<object> rows, CancellationToken cancellationToken)
    {
        var list = rows.ToList();
        foreach (var j in db.SparkModel.Joins)
        {
            var owners = list.Where(j.Owner.IsInstanceOfType).ToList();
            if (owners.Count == 0) continue;
            var ids = owners.Select(o => GetId(o)!).ToList();
            NumberOfRequests++;
            var links = await db.Set<Dictionary<string, object>>(j.Table).AsNoTracking()
                .Where(r => ids.Contains(EF.Property<string>(r, "OwnerId")))
                .Select(r => new { Owner = EF.Property<string>(r, "OwnerId"), Target = EF.Property<string>(r, "TargetId") })
                .ToListAsync(cancellationToken);
            var byOwner = links.ToLookup(l => l.Owner, l => l.Target);
            foreach (var o in owners) j.PropertyInfo.SetValue(o, byOwner[GetId(o)!].OrderBy(t => t, StringComparer.Ordinal).ToList());
        }
    }

    public void Dispose() => db.Dispose();
}

/// <summary>
/// The query provider: EF translates the pipeline's LINQ as it is. Two Spark-specific pieces: the join-table
/// rewrite (D26) and search as LIKE (S10's first step).
/// </summary>
public sealed class EfSparkQueryProvider(ISparkSession session) : ISparkQueryProvider
{
    private EfSparkSession Session => (EfSparkSession)session;

    public bool IsStoreQuery(IQueryable source) => source.Provider is IAsyncQueryProvider;

    public IQueryable OpenIndex(Type entityType, Type indexType, bool narrowToCollection)
        => throw new NotSupportedException("Views are spike S5.");

    public IQueryable Project(IQueryable source, Type resultType) => source;

    // No navigation properties (D26): referenced rows are batch-loaded by id after the query.
    public IQueryable Include(IQueryable source, IReadOnlyCollection<string> paths) => source;

    // ids.Contains(x.Id) on the key column. The list goes in as a captured member, not Expression.Constant: EF inlines
    // a constant list as literals (one query plan per id set) and parameterizes a captured one.
    public Expression In(Expression member, IReadOnlyList<string> values)
        => Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), [typeof(string)],
            Expression.Field(Expression.Constant(new Captured(values.ToList())), nameof(Captured.Values)), member);

    private sealed class Captured(List<string> values) { public readonly List<string> Values = values; }
    private sealed class CapturedWord(string value) { public readonly string Value = value; }

    public IReadOnlySet<string>? IndexedFields(IQueryable source) => null;

    /// <summary>
    /// RavenDB's semantics (QueryExecutor.cs:1944): every word must occur in one field, any field may match. Case-insensitive
    /// on both databases (lower on both sides), and Contains escapes the term's LIKE wildcards. Non-string members are skipped.
    /// </summary>
    public IQueryable? Search(IQueryable source, IReadOnlyList<PropertyInfo> properties, string term)
    {
        // QueryExecutor hands over RavenDB's syntax, "*word*" per word (QueryExecutor.cs:2004): finding for M2.
        var words = term.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.Trim('*').ToLowerInvariant())
            .Where(w => w.Length > 0).ToList();
        var strings = properties.Where(p => p.PropertyType == typeof(string)).ToList();
        if (words.Count == 0 || strings.Count == 0) return null;

        var x = Expression.Parameter(source.ElementType, "x");
        var toLower = typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes)!;
        var contains = typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!;
        Expression? any = null;
        foreach (var p in strings)
        {
            var member = Expression.Property(x, p);
            Expression all = Expression.NotEqual(member, Expression.Constant(null, typeof(string)));
            foreach (var w in words)
                all = Expression.AndAlso(all, Expression.Call(Expression.Call(member, toLower), contains,
                    Expression.Field(Expression.Constant(new CapturedWord(w)), nameof(CapturedWord.Value))));
            any = any is null ? all : Expression.OrElse(any, all);
        }

        var where = Expression.Call(typeof(Queryable), nameof(Queryable.Where), [source.ElementType], source.Expression, Expression.Quote(Expression.Lambda(any!, x)));
        return source.Provider.CreateQuery(where);
    }

    public Task<int> CountAsync(IQueryable source, CancellationToken cancellationToken)
        => (Task<int>)Generic(nameof(EfSparkSession.CountAsync), source.ElementType).Invoke(Session, [source, cancellationToken])!;

    public async Task<IReadOnlyList<object>> ToListAsync(IQueryable source, CancellationToken cancellationToken)
    {
        var task = (Task)Generic(nameof(EfSparkSession.ToListAsync), source.ElementType).Invoke(Session, [source, cancellationToken])!;
        await task;
        return [.. ((System.Collections.IEnumerable)task.GetType().GetProperty("Result")!.GetValue(task)!).Cast<object>()];
    }

    private static MethodInfo Generic(string name, Type t) => typeof(EfSparkSession).GetMethods().Single(m => m.Name == name && m.IsGenericMethod).MakeGenericMethod(t);
}

/// <summary>
/// D26's query half: <c>x.Skills</c> (a CLR list EF ignores) becomes a subquery over the join table. Covers the shapes
/// QueryExecutor emits for a collection column: <c>x.L != null</c>, <c>Any(x.L, e =&gt; …)</c>, <c>Contains</c>.
/// </summary>
public sealed class JoinRewriter(SparkEfContext db) : ExpressionVisitor
{
    public static IQueryable<T> Rewrite<T>(SparkEfContext db, IQueryable<T> query)
    {
        if (db.SparkModel.Joins.Count == 0) return query;
        var rewritten = new JoinRewriter(db).Visit(query.Expression);
        return ReferenceEquals(rewritten, query.Expression) ? query : query.Provider.CreateQuery<T>(rewritten);
    }

    private (JoinCollection Join, Expression Owner)? AsJoin(Expression? e)
        => e is MemberExpression { Member: PropertyInfo p, Expression: { } owner }
           && db.SparkModel.Joins.FirstOrDefault(j => j.Property == p.Name && j.Owner.IsAssignableFrom(owner.Type)) is { } join
            ? (join, owner) : null;

    private Expression Subquery(JoinCollection j, Expression owner)
    {
        var root = db.Set<Dictionary<string, object>>(j.Table).AsQueryable().Expression;
        var r = Expression.Parameter(typeof(Dictionary<string, object>), "r");
        var prop = typeof(EF).GetMethod(nameof(EF.Property))!.MakeGenericMethod(typeof(string));
        var ownerId = Expression.Call(prop, Expression.Convert(owner, typeof(object)), Expression.Constant("Id"));
        var where = Expression.Call(typeof(Queryable), nameof(Queryable.Where), [r.Type], root,
            Expression.Quote(Expression.Lambda(Expression.Equal(Expression.Call(prop, r, Expression.Constant("OwnerId")), ownerId), r)));
        return Expression.Call(typeof(Queryable), nameof(Queryable.Select), [r.Type, typeof(string)], where,
            Expression.Quote(Expression.Lambda(Expression.Call(prop, r, Expression.Constant("TargetId")), r)));
    }

    protected override Expression VisitBinary(BinaryExpression node)
    {
        // A join list always exists (possibly empty): x.L != null is true, x.L == null is false.
        if (node.NodeType is ExpressionType.NotEqual or ExpressionType.Equal
            && (AsJoin(node.Left) is not null && node.Right is ConstantExpression { Value: null }
                || AsJoin(node.Right) is not null && node.Left is ConstantExpression { Value: null }))
            return Expression.Constant(node.NodeType == ExpressionType.NotEqual);
        return base.VisitBinary(node);
    }

    protected override Expression VisitMethodCall(MethodCallExpression node)
    {
        if (node.Method.DeclaringType == typeof(Enumerable) && node.Arguments.Count > 0 && AsJoin(node.Arguments[0]) is var (join, owner)
            && node.Method.Name is nameof(Enumerable.Any) or nameof(Enumerable.Contains) or nameof(Enumerable.Count))
        {
            var sub = Subquery(join, Visit(owner));
            var rest = node.Arguments.Skip(1).Select(a => a is LambdaExpression l ? (Expression)Expression.Quote((LambdaExpression)Visit(l)) : Visit(a));
            return Expression.Call(typeof(Queryable), node.Method.Name, [typeof(string)], [sub, .. rest]);
        }

        if (node.Method.Name == nameof(List<string>.Contains) && node.Object is not null && AsJoin(node.Object) is var (j2, o2))
            return Expression.Call(typeof(Queryable), nameof(Queryable.Contains), [typeof(string)], Subquery(j2, Visit(o2)), Visit(node.Arguments[0]));

        return base.VisitMethodCall(node);
    }
}

/// <summary>
/// D30: a database constraint error becomes Spark's validation error naming the attribute; a duplicate primary key on an
/// insert-only store is a concurrency failure (D3). The raw database message never leaves: SQL Server's truncation
/// message carries the value itself.
/// </summary>
public static partial class ConstraintErrors
{
    private enum Kind { Unique, ForeignKey, NotNull, Length }

    /// <summary>The raw database messages seen (spike evidence only: they can hold values and never leave the server).</summary>
    public static readonly List<string> Seen = [];

    public static Exception Translate(DbContext db, DbUpdateException ex)
    {
        lock (Seen) Seen.Add(ex.InnerException?.Message ?? ex.Message);
        // PostgreSQL names the constraint and the column in fields; SQL Server only inside its (localized) message text.
        (Kind? Kind, string[] Names) error = ex.InnerException switch
        {
            Microsoft.Data.SqlClient.SqlException s => s.Number switch
            {
                2601 or 2627 => (Kind.Unique, Quoted(s.Message)),
                547 => (Kind.ForeignKey, Quoted(s.Message)),
                515 => (Kind.NotNull, Quoted(s.Message)),
                2628 or 8152 => (Kind.Length, []),
                _ => (null, []),
            },
            Npgsql.PostgresException p => p.SqlState switch
            {
                "23505" => (Kind.Unique, [p.ConstraintName!]),
                "23503" => (Kind.ForeignKey, [p.ConstraintName!]),
                "23502" => (Kind.NotNull, [p.ColumnName!]),
                "22001" => (Kind.Length, []),
                _ => (null, []),
            },
            _ => (null, []),
        };
        var (kind, names) = error;
        if (kind is null) return ex;
        var model = db.Model;
        // Failed entries first: a column name such as "Name" exists on several tables.
        var failed = ex.Entries.Select(e => e.Metadata).ToHashSet();
        IEnumerable<Microsoft.EntityFrameworkCore.Metadata.IEntityType> types
            = [.. failed, .. model.GetEntityTypes().Where(t => !failed.Contains(t))];

        switch (kind)
        {
            case Kind.Unique:
                if (types.SelectMany(t => t.GetKeys()).Any(k => k.IsPrimaryKey() && names.Contains(k.GetName())))
                    return new SparkStoreConcurrencyException("A row with this id exists already.", ex);
                var index = types.SelectMany(t => t.GetIndexes()).FirstOrDefault(i => names.Contains(i.GetDatabaseName()));
                return Validation(index?.Properties[0].Name, "must be unique", ex);
            case Kind.ForeignKey:
                var fk = types.SelectMany(t => t.GetForeignKeys()).FirstOrDefault(f => names.Contains(f.GetConstraintName()));
                return Validation(fk?.Properties[0].Name, "refers to a row that does not exist", ex);
            case Kind.NotNull:
                var property = types.SelectMany(t => t.GetProperties()).FirstOrDefault(p => names.Contains(p.GetColumnName()));
                return Validation(property?.Name, "is required", ex);
            default:
                // Neither database names the column reliably (PostgreSQL never does): find the value that is too long.
                var tooLong = ex.Entries.SelectMany(e => e.Properties)
                    .FirstOrDefault(p => p.Metadata.GetMaxLength() is int max && p.CurrentValue is string s && s.Length > max);
                return Validation(tooLong?.Metadata.Name, $"is longer than {tooLong?.Metadata.GetMaxLength()} characters", ex);
        }
    }

    private static SparkValidationException Validation(string? attribute, string rule, Exception inner)
        // SparkValidationException takes no inner exception: the database's text stays in the server log only.
        => new($"{attribute ?? "A value"} {rule}.", attribute);

    /// <summary>
    /// The identifier-like words of a SQL Server message. The message is localized by the session's language (and not
    /// every language quotes the names), so no keyword or quote is relied on: every word is offered, and the model picks
    /// the constraint, index or column it knows.
    /// </summary>
    private static string[] Quoted(string message)
        => [.. Word().Matches(message).Select(m => m.Value)];

    [System.Text.RegularExpressions.GeneratedRegex("[A-Za-z0-9_]+")]
    private static partial System.Text.RegularExpressions.Regex Word();
}

public static class SqlLog
{
    public static readonly List<string> Commands = [];
    public static void Add(string s) { lock (Commands) Commands.Add(s); }
    public static void Clear() { lock (Commands) Commands.Clear(); }
    public static string Last(int n = 1) { lock (Commands) return string.Join("\n---\n", Commands.TakeLast(n)); }
    public static string All() { lock (Commands) return string.Join("\n---\n", Commands); }

    public static DbContextOptionsBuilder Log(this DbContextOptionsBuilder b)
        => b.LogTo(Add, [RelationalEventId.CommandExecuted]).EnableSensitiveDataLogging(false);
}
