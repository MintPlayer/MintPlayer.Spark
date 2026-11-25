using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Relational.Model;

namespace MintPlayer.Spark.Relational.Storage;

/// <summary>
/// A unit of work over one <see cref="SparkDbContext"/> (#536 D1). Entities are tracked by EF; the session adds what a
/// document store does and EF doesn't:
/// <list type="bullet">
/// <item>D3: the aggregate's version, moved when its row, a child row or a join row changes; <c>""</c> as insert-only.</item>
/// <item>A store of an instance whose row exists (not loaded in this session) overwrites that row, as a document PUT.</item>
/// <item>D26: reference lists written to and read from their join tables.</item>
/// <item>D30: constraint errors as validation messages.</item>
/// </list>
/// </summary>
public sealed partial class RelationalSparkSession : ISparkSession
{
    private readonly SparkDbContext db;

    /// <summary>Entities stored with an expected version: <c>""</c> (must not exist) or a version (must match).</summary>
    private readonly Dictionary<object, string> expectations = new(ReferenceEqualityComparer.Instance);

    /// <summary>Deletes by id without a version: the commit reads the current version first.</summary>
    private readonly HashSet<object> unversionedDeletes = new(ReferenceEqualityComparer.Instance);

    /// <summary>A stored instance whose row existed, mapped to the loaded instance its values were copied onto.</summary>
    private readonly Dictionary<object, object> aliases = new(ReferenceEqualityComparer.Instance);

    private readonly RelationalSparkStore store;

    public RelationalSparkSession(SparkDbContext db, RelationalSparkStore store)
    {
        this.db = db;
        this.store = store;
    }

    public SparkDbContext Context => db;

    public object Native => db;

    private SparkRelationalModel Model => db.SparkModel;

    public int NumberOfRequests { get; private set; }

    /// <summary>RavenDB's default (30), so a code path that would exhaust a RavenDB session fails here too.</summary>
    public int MaxNumberOfRequests { get; set; } = 30;

    /// <summary>A loaded row is always checked (its version is EF's concurrency token); this adds the new-row check.</summary>
    public bool OptimisticConcurrency { get; set; }

    /// <summary>Set while one session call issues several statements (a save, a load's join tables): they count as one.</summary>
    private bool quiet;

    private void Request()
    {
        if (quiet)
            return;
        if (++NumberOfRequests > MaxNumberOfRequests)
            throw new InvalidOperationException(
                $"The maximum number of requests ({MaxNumberOfRequests}) allowed for this session has been reached. "
                + "Use SparkSessionExtensions.IgnoreMaxRequests() around a path that needs more.");
    }

    public event Action<object>? BeforeStore;

    public event Action<object>? BeforeDelete;

    // ---- queries ----

    public IQueryable<T> Query<T>() where T : class
    {
        Model.RequireTable(typeof(T));
        return db.Set<T>();
    }

    /// <summary>A view (D32) is its row type's set, mapped to the SQL view; rows are read-only, so untracked.</summary>
    public IQueryable<T> Query<T, TIndex>() where T : class where TIndex : class, new()
    {
        var view = ViewOf(typeof(TIndex));
        if (view.RowType != typeof(T))
            throw new InvalidOperationException($"View {view.Name} yields {view.RowType.Name}, not {typeof(T).Name}.");
        return db.Set<T>().AsNoTracking();
    }

    /// <summary>The mapped view declared by <paramref name="viewType"/>, or why there is none.</summary>
    internal Abstractions.Views.ISparkView ViewOf(Type viewType)
    {
        if (Model.Views.FirstOrDefault(v => v.GetType() == viewType) is { } view)
            return view;
        if (Model.UnmappedViews.TryGetValue(viewType, out var reason))
            throw new InvalidOperationException(reason);
        throw typeof(Abstractions.Views.ISparkView).IsAssignableFrom(viewType)
            ? new InvalidOperationException($"View {viewType.Name} is not in this store's model: declare it in an index assembly of the app.")
            : new NotSupportedException(
                $"The relational providers have no native indexes: {viewType.Name} cannot be queried. Declare it as a SparkView<TRow>.");
    }

    /// <summary>The rows of a view, untyped (the query pipeline's <c>OpenIndex</c>).</summary>
    internal IQueryable ViewRows(Type viewType)
    {
        var view = ViewOf(viewType);
        var set = (IQueryable)typeof(DbContext).GetMethod(nameof(DbContext.Set), Type.EmptyTypes)!.MakeGenericMethod(view.RowType).Invoke(db, [])!;
        return (IQueryable)typeof(EntityFrameworkQueryableExtensions).GetMethod(nameof(EntityFrameworkQueryableExtensions.AsNoTracking))!
            .MakeGenericMethod(view.RowType).Invoke(null, [set])!;
    }

    public async Task<List<T>> ToListAsync<T>(IQueryable<T> query, CancellationToken cancellationToken = default)
    {
        Request();
        var rows = await JoinRewriter.Rewrite(db, query).ToListAsync(cancellationToken);
        await HydrateAsync(rows.Cast<object>(), cancellationToken);
        return rows;
    }

    public Task<int> CountAsync<T>(IQueryable<T> query, CancellationToken cancellationToken = default)
    {
        Request();
        return JoinRewriter.Rewrite(db, query).CountAsync(cancellationToken);
    }

    // ---- loads ----

    public async Task<T?> LoadAsync<T>(string id, CancellationToken cancellationToken = default) where T : class
        => (await LoadAsync<T>([id], cancellationToken))[id];

    public async Task<IReadOnlyDictionary<string, T?>> LoadAsync<T>(IEnumerable<string> ids, CancellationToken cancellationToken = default) where T : class
    {
        var requested = ids.Where(id => id is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var table = Model.RequireTable(typeof(T));
        var keys = requested.Select(table.KeyValue).OfType<object>().ToList();
        var found = new Dictionary<string, T>(StringComparer.Ordinal);
        if (keys.Count > 0)
        {
            Request();
            var rows = await WhereKeyIn(db.Set<T>(), table, keys).ToListAsync(cancellationToken);
            await HydrateAsync(rows, cancellationToken);
            foreach (var row in rows)
                found[GetId(row)!] = row;
        }
        var result = new Dictionary<string, T?>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in requested)
            result[id] = found.GetValueOrDefault(id.ToLowerInvariant());
        return result;
    }

    public Task<IReadOnlyDictionary<string, T?>> LoadAsync<T>(IEnumerable<string> ids, IReadOnlyCollection<string> includePaths, CancellationToken cancellationToken = default) where T : class
        => LoadAsync<T>(ids, cancellationToken);

    /// <summary>Nothing to preload: a load is one query per type, and references are batch-loaded by the caller (D23 rule 7).</summary>
    public Task PreloadAsync(IReadOnlyCollection<(Type EntityType, string Id)> documents, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task<object?> LoadAsync(Type entityType, string id, CancellationToken cancellationToken = default)
        => (await LoadAsync(entityType, [id], cancellationToken))[id];

    public async Task<IReadOnlyDictionary<string, object?>> LoadAsync(Type entityType, IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        var task = (Task)LoadManyMethod.MakeGenericMethod(entityType).Invoke(this, [ids, cancellationToken])!;
        await task;
        var typed = (System.Collections.IDictionary)task.GetType().GetProperty(nameof(Task<object>.Result))!.GetValue(task)!;
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in typed)
            result[(string)entry.Key] = entry.Value;
        return result;
    }

    private static readonly MethodInfo LoadManyMethod = typeof(RelationalSparkSession).GetMethods()
        .Single(m => m.Name == nameof(LoadAsync) && m.IsGenericMethod && m.GetParameters() is [{ ParameterType: var p }, _] && p == typeof(IEnumerable<string>));

    public async Task<IReadOnlyList<T>> LoadStartingWithAsync<T>(string idPrefix, int start = 0, int pageSize = 25, CancellationToken cancellationToken = default) where T : class
    {
        var table = Model.RequireTable(typeof(T));
        var prefix = idPrefix.ToLowerInvariant();
        IQueryable<T> query;
        if (table.Key == SparkKeyShape.Text || (table.Key == SparkKeyShape.Natural && prefix.StartsWith(table.Prefix, StringComparison.Ordinal)))
        {
            // D7: an escaped LIKE on the key column, so the prefix's own % and _ match themselves.
            var keyPrefix = table.Key == SparkKeyShape.Text ? prefix : prefix[table.Prefix.Length..];
            query = KeyLike(table, typeof(T), keyPrefix).Cast<T>();
        }
        else if (table.Prefix.StartsWith(prefix, StringComparison.Ordinal))
            query = db.Set<T>(); // the prefix covers the whole collection
        else if (table.Key == SparkKeyShape.Natural)
            return [];
        else
            throw new NotSupportedException(
                $"{typeof(T).Name} has {table.Key} ids: a prefix scan can cover the whole collection ('{table.Prefix}'), not part of a key.");

        Request();
        var rows = await query.OrderBy(x => EF.Property<object>(x, table.IdProperty.Name)).Skip(start).Take(pageSize).ToListAsync(cancellationToken);
        await HydrateAsync(rows, cancellationToken);
        return rows;
    }

    /// <summary><c>SELECT * FROM table WHERE key LIKE @p ESCAPE '\'</c>: the table and column come from the model, the value is a parameter.</summary>
    private IQueryable KeyLike(SparkTable table, Type type, string keyPrefix)
    {
        var helper = db.GetService<Microsoft.EntityFrameworkCore.Storage.ISqlGenerationHelper>();
        var entityType = db.Model.FindEntityType(table.Root)!;
        var column = entityType.FindProperty(table.IdProperty.Name)!.GetColumnName();
        var sql = $"SELECT * FROM {helper.DelimitIdentifier(table.Name, entityType.GetSchema())} WHERE {helper.DelimitIdentifier(column)} LIKE {{0}} ESCAPE '\\'";
        var pattern = EscapeLike(keyPrefix) + "%";
        var rooted = (IQueryable)FromSqlMethod.MakeGenericMethod(table.Root).Invoke(null, [SetOf(table.Root), sql, new object[] { pattern }])!;
        return type == table.Root ? rooted : (IQueryable)OfTypeMethod.MakeGenericMethod(type).Invoke(null, [rooted])!;
    }

    private static readonly MethodInfo FromSqlMethod = typeof(RelationalQueryableExtensions).GetMethod(nameof(RelationalQueryableExtensions.FromSqlRaw))!;
    private static readonly MethodInfo OfTypeMethod = typeof(Queryable).GetMethod(nameof(Queryable.OfType))!;

    internal static string EscapeLike(string text) => text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");

    private IQueryable SetOf(Type type)
        => (IQueryable)typeof(DbContext).GetMethod(nameof(DbContext.Set), Type.EmptyTypes)!.MakeGenericMethod(type).Invoke(db, [])!;

    public Task<bool> ExistsAsync<T>(Id<T> id, CancellationToken cancellationToken = default) where T : class
        => ExistsAsync(typeof(T), id.Value, cancellationToken);

    public async Task<bool> ExistsAsync(Type entityType, string id, CancellationToken cancellationToken = default)
        => (await VersionsOfAsync(entityType, [id], cancellationToken)).Count > 0;

    // ---- versions ----

    public string? GetVersion(object entity)
    {
        var entry = TrackedEntry(entity) ?? throw new InvalidOperationException("The session does not track this entity.");
        return entry.State == EntityState.Added ? null : FormatVersion((long)entry.Property(SparkDbContext.VersionProperty).CurrentValue!);
    }

    public Task<IReadOnlyDictionary<string, string>> GetVersionsAsync<T>(IEnumerable<Id<T>> ids, CancellationToken cancellationToken = default) where T : class
        => GetVersionsAsync(typeof(T), ids.Select(i => i.Value), cancellationToken);

    public async Task<IReadOnlyDictionary<string, string>> GetVersionsAsync(Type entityType, IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var wanted = ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var missing = new List<string>();
        foreach (var id in wanted)
        {
            // A row this session tracks answers from the tracker (S4): its version as this session will write it.
            if (TrackedById(entityType, id) is { State: not EntityState.Added and not EntityState.Detached } entry)
                result[id] = FormatVersion((long)entry.Property(SparkDbContext.VersionProperty).OriginalValue!);
            else
                missing.Add(id);
        }
        foreach (var (id, version) in await VersionsOfAsync(entityType, missing, cancellationToken))
            result[id] = FormatVersion(version);
        return result;
    }

    internal static string FormatVersion(long version) => version.ToString(CultureInfo.InvariantCulture);

    private static long ParseVersion(string version)
        => long.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new SparkStoreConcurrencyException("The version is not one this store issued.");

    /// <summary>The stored versions of the ids that exist, keyed by the lowercase id.</summary>
    private async Task<Dictionary<string, long>> VersionsOfAsync(Type entityType, IReadOnlyCollection<string> ids, CancellationToken cancellationToken)
    {
        var table = Model.RequireTable(entityType);
        var keys = ids.Select(table.KeyValue).OfType<object>().ToList();
        if (keys.Count == 0)
            return [];
        Request();
        var task = (Task<Dictionary<string, long>>)VersionsOfTypedMethod.MakeGenericMethod(table.Root, table.IdProperty.PropertyType)
            .Invoke(this, [table, keys, cancellationToken])!;
        return await task;
    }

    private static readonly MethodInfo VersionsOfTypedMethod = typeof(RelationalSparkSession).GetMethod(nameof(VersionsOfTypedAsync), BindingFlags.NonPublic | BindingFlags.Instance)!;

    private async Task<Dictionary<string, long>> VersionsOfTypedAsync<T, TKey>(SparkTable table, List<object> keys, CancellationToken cancellationToken) where T : class
    {
        var rows = await WhereKeyIn(db.Set<T>().AsNoTracking(), table, keys)
            .Select(x => new { Id = EF.Property<TKey>(x, table.IdProperty.Name), Version = EF.Property<long>(x, SparkDbContext.VersionProperty) })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(r => SparkIdValues.ToText(r.Id)!, r => r.Version, StringComparer.OrdinalIgnoreCase);
    }

    // ---- writes ----

    public Task StoreAsync(object entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (entity is string)
            throw new ArgumentException("StoreAsync takes an entity, not an id.", nameof(entity));
        if (aliases.ContainsKey(entity) || TrackedEntry(entity) is { State: not EntityState.Detached })
            return Task.CompletedTask;

        var table = Model.RequireTable(entity.GetType());
        if (SparkIdValues.ReadEntityId(entity) is not { Length: > 0 } id)
            id = NewId(entity, table);
        else if (table.KeyValue(id) is null)
            throw new InvalidOperationException($"'{id}' is not an id of collection {table.Name}.");
        SparkIdValues.WriteEntityId(entity, id.ToLowerInvariant());
        db.Add(entity);
        return Task.CompletedTask;
    }

    public Task StoreAsync(object entity, string id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (TrackedEntry(entity) is null or { State: EntityState.Detached })
            SparkIdValues.WriteEntityId(entity, id.ToLowerInvariant());
        return StoreAsync(entity, cancellationToken);
    }

    /// <summary>
    /// D3: <c>""</c> inserts (the row must not exist); a version makes the write carry <c>WHERE Version = @expected</c>;
    /// <see langword="null"/> checks nothing.
    /// </summary>
    public async Task StoreAsync(object entity, string? expectedVersion, string id, CancellationToken cancellationToken = default)
    {
        await StoreAsync(entity, id, cancellationToken);
        if (expectedVersion is not null)
            expectations[Resolve(entity)] = expectedVersion;
    }

    private static string NewId(object entity, SparkTable table)
    {
        if (entity is IHasNaturalId natural)
            return natural.GetId().ToLowerInvariant();
        return table.Key switch
        {
            SparkKeyShape.Guid or SparkKeyShape.Text => table.Prefix + Guid.NewGuid().ToString("N"),
            SparkKeyShape.Natural => throw new InvalidOperationException($"{table.Root.Name} has natural ids: set its Id before storing it."),
            _ => throw new InvalidOperationException($"{table.Root.Name} has integer ids: take one from ISparkIds.NewAsync before storing it."),
        };
    }

    public void Delete<T>(Id<T> id, string? expectedVersion = null) where T : class
        => Delete(typeof(T), id.Value, expectedVersion);

    public void Delete(Type entityType, string id, string? expectedVersion = null)
    {
        var table = Model.RequireTable(entityType);
        var key = table.KeyValue(id) ?? throw new InvalidOperationException($"'{id}' is not an id of collection {table.Name}.");
        object entity;
        if (TrackedById(entityType, id) is { } tracked)
            entity = tracked.Entity;
        else
        {
            // A key-only stub: DELETE … WHERE Id = @id AND Version = @v. Child and join rows go by ON DELETE CASCADE.
            entity = Activator.CreateInstance(table.Root, nonPublic: true)
                ?? throw new InvalidOperationException($"{table.Root.Name} needs a parameterless constructor to be deleted by id.");
            table.IdProperty.SetValue(entity, key);
            db.Attach(entity);
            if (expectedVersion is null)
                unversionedDeletes.Add(entity);
        }
        if (expectedVersion is not null)
            db.Entry(entity).Property(SparkDbContext.VersionProperty).OriginalValue = ParseVersion(expectedVersion);
        Delete(entity);
    }

    public void Delete(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (entity is string)
            throw new ArgumentException("Delete(object) takes an entity; delete by id with Delete<T>(Id<T>) or Delete(Type, id).", nameof(entity));
        var target = Resolve(entity);
        BeforeDelete?.Invoke(target);
        if (db.Entry(target).State == EntityState.Added)
            db.Entry(target).State = EntityState.Detached;
        else
            db.Remove(target);
    }

    /// <summary>
    /// What the save writes, for <see cref="ISparkChangeFeed.OnCommitted"/> (D6): each stored root with its new version,
    /// each deleted one with none. A deleted root also gets its tombstone row here, in the same transaction.
    /// </summary>
    private List<SparkStoreChange> CommittedChanges()
    {
        var changes = new List<SparkStoreChange>();
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in Roots().ToList())
        {
            var table = Model.RequireTable(entry.Metadata.ClrType);
            switch (entry.State)
            {
                case EntityState.Added or EntityState.Modified:
                    changes.Add(new(GetId(entry.Entity), table.Name, FormatVersion((long)entry.Property(SparkDbContext.VersionProperty).CurrentValue!)));
                    break;
                case EntityState.Deleted when GetId(entry.Entity) is { } id:
                    changes.Add(new(id, table.Name, null));
                    db.Add(RelationalTombstones.For(table, id, now));
                    break;
            }
        }
        return changes;
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        List<SparkStoreChange> changes;
        db.ChangeTracker.DetectChanges();
        foreach (var entry in Roots().Where(e => e.State is EntityState.Added or EntityState.Modified).ToList())
            BeforeStore?.Invoke(entry.Entity);
        if (store.BeforeSaveHooks is { Count: > 0 } hooks)
        {
            db.ChangeTracker.DetectChanges();
            foreach (var entry in Roots().Where(e => e.State is EntityState.Added or EntityState.Modified).ToList())
            {
                var expiry = entry.Property(SparkDbContext.ExpiresProperty);
                foreach (var hook in hooks)
                    hook(entry.Entity, entry.State == EntityState.Added, value => expiry.CurrentValue = value?.ToUniversalTime());
            }
        }

        Request();
        quiet = true;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await ResolveExistingRowsAsync(cancellationToken);
            await ResolveUnversionedDeletesAsync(cancellationToken);
            await ApplyPatchesAsync(cancellationToken);
            var changedOwners = await WriteJoinRowsAsync(cancellationToken);
            MoveVersions(changedOwners);
            await ApplyReservationsAsync(cancellationToken);
            changes = CommittedChanges();

            await WriteAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            quiet = false;
        }
        db.ChangeTracker.AcceptAllChanges();
        foreach (var tombstone in db.ChangeTracker.Entries<SparkTombstoneRow>().ToList())
            tombstone.State = EntityState.Detached;
        store.RaiseCommitted(changes);
        expectations.Clear();
        unversionedDeletes.Clear();
        patches.Clear();
        reservations.Clear();
    }

    private async Task WriteAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(acceptAllChangesOnSuccess: false, cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.Entries.FirstOrDefault(e => e.Entity is SparkReservationRow) is { Entity: SparkReservationRow row })
        {
            // Another unit of work claimed or moved the key between our read and our write.
            throw new SparkReservationTakenException(row.Key, "a concurrent unit of work");
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new SparkStoreConcurrencyException("The row changed or was deleted since it was read.", ex);
        }
        catch (DbUpdateException ex)
        {
            throw SparkConstraintErrors.Translate(db, db.Setup.Dialect, ex);
        }
    }

    /// <summary>
    /// An added root whose row exists: with <c>""</c> a concurrency failure; otherwise its values overwrite the row (a
    /// document PUT), through the loaded row so child rows are replaced, not inserted twice.
    /// </summary>
    private async Task ResolveExistingRowsAsync(CancellationToken cancellationToken)
    {
        var added = Roots().Where(e => e.State == EntityState.Added).ToList();
        foreach (var group in added.GroupBy(e => Model.RequireTable(e.Entity.GetType())))
        {
            var ids = group.Select(e => GetId(e.Entity)!).ToList();
            var existing = await VersionsOfAsync(group.Key.Root, ids, cancellationToken);
            foreach (var entry in group)
            {
                var id = GetId(entry.Entity)!;
                var expected = expectations.GetValueOrDefault(entry.Entity) ?? (OptimisticConcurrency ? "" : null);
                if (!existing.TryGetValue(id, out var stored))
                {
                    if (expected is { Length: > 0 })
                        throw new SparkStoreConcurrencyException($"'{id}' was expected to exist at version {expected}.");
                    continue;
                }
                if (expected == "")
                    throw new SparkStoreConcurrencyException($"'{id}' was expected not to exist.");

                var incoming = entry.Entity;
                var expiry = entry.Property(SparkDbContext.ExpiresProperty).CurrentValue; // a PUT replaces it, as on RavenDB
                entry.State = EntityState.Detached;
                foreach (var owned in OwnedEntries(id))
                    owned.State = EntityState.Detached;
                var loaded = (await LoadAsync(incoming.GetType(), id, cancellationToken))
                    ?? throw new SparkStoreConcurrencyException($"'{id}' was deleted while it was being stored.");
                CopyOnto(incoming, loaded);
                db.Entry(loaded).Property(SparkDbContext.ExpiresProperty).CurrentValue = expiry;
                aliases[incoming] = loaded;
                var version = db.Entry(loaded).Property(SparkDbContext.VersionProperty);
                version.OriginalValue = expected is { Length: > 0 } ? ParseVersion(expected) : stored;
                if (expected is not null)
                {
                    expectations.Remove(incoming);
                    expectations[loaded] = expected;
                }
            }
        }

        // A loaded row stored with a version: the UPDATE checks that version, not the one it was loaded at.
        foreach (var (entity, expected) in expectations.Where(e => e.Value.Length > 0).ToList())
            if (db.Entry(entity) is { State: EntityState.Unchanged or EntityState.Modified } entry)
            {
                entry.Property(SparkDbContext.VersionProperty).OriginalValue = ParseVersion(expected);
                entry.State = EntityState.Modified;
            }
    }

    /// <summary>Copies a stored instance's members onto the loaded one: scalars, child collections and reference lists.</summary>
    private void CopyOnto(object incoming, object loaded)
    {
        var entry = db.Entry(loaded);
        entry.CurrentValues.SetValues(incoming);
        foreach (var navigation in entry.Navigations.Where(n => n.Metadata.TargetEntityType.IsOwned()))
            navigation.CurrentValue = navigation.Metadata.PropertyInfo!.GetValue(incoming);
        foreach (var join in Model.Joins.Where(j => j.Owner.IsInstanceOfType(loaded)))
            join.Property.SetValue(loaded, join.Property.GetValue(incoming));
    }

    private async Task ResolveUnversionedDeletesAsync(CancellationToken cancellationToken)
    {
        foreach (var group in unversionedDeletes.Where(e => db.Entry(e).State == EntityState.Deleted).GroupBy(e => Model.RequireTable(e.GetType())))
        {
            var current = await VersionsOfAsync(group.Key.Root, [.. group.Select(e => GetId(e)!)], cancellationToken);
            foreach (var entity in group)
            {
                if (current.TryGetValue(GetId(entity)!, out var version))
                    db.Entry(entity).Property(SparkDbContext.VersionProperty).OriginalValue = version;
                else
                    db.Entry(entity).State = EntityState.Detached; // already gone: nothing to delete, as on RavenDB
            }
        }
    }

    /// <summary>D26: rewrites an owner's join rows when its list differs from what is stored. Returns the owners changed.</summary>
    private async Task<HashSet<object>> WriteJoinRowsAsync(CancellationToken cancellationToken)
    {
        var changed = new HashSet<object>(ReferenceEqualityComparer.Instance);
        if (Model.Joins.Count == 0)
            return changed;
        var owners = Roots().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Unchanged).Select(e => e.Entity).ToList();
        foreach (var join in Model.Joins)
        {
            var mine = owners.Where(join.Owner.IsInstanceOfType).ToList();
            if (mine.Count == 0)
                continue;
            var owner = Model.RequireTable(join.Owner);
            var set = db.Set<Dictionary<string, object>>(join.Name);
            var ownerKeys = mine.Select(o => owner.IdProperty.GetValue(o)!).ToList();
            Request();
            var stored = await set.Where(JoinOwnerIn(owner, ownerKeys)).ToListAsync(cancellationToken);
            var byOwner = stored.ToLookup(r => SparkIdValues.ToText(r[SparkDbContext.OwnerIdProperty])!, StringComparer.OrdinalIgnoreCase);
            foreach (var entity in mine)
            {
                var ownerId = GetId(entity)!;
                var wanted = SparkIdValues.ToTexts(join.Property.GetValue(entity)).Select(t => t.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
                var existing = byOwner[ownerId].ToList();
                if (wanted.SetEquals(existing.Select(r => SparkIdValues.ToText(r[SparkDbContext.TargetIdProperty])!.ToLowerInvariant())))
                    continue;
                set.RemoveRange(existing);
                foreach (var target in wanted)
                    set.Add(new Dictionary<string, object>
                    {
                        [SparkDbContext.OwnerIdProperty] = owner.IdProperty.GetValue(entity)!,
                        [SparkDbContext.TargetIdProperty] = SparkIdValues.FromText(join.ElementType, target)!,
                    });
                changed.Add(entity);
            }
        }
        return changed;
    }

    /// <summary>D3: the aggregate's version moves when its row, a child row (D25) or a join row (D26) changes.</summary>
    private void MoveVersions(HashSet<object> changedOwners)
    {
        foreach (var owned in db.ChangeTracker.Entries().Where(e => e.Metadata.IsOwned() && e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList())
        {
            var ownerKey = owned.Property(SparkDbContext.OwnerIdProperty);
            var ownerId = SparkIdValues.ToText(ownerKey.CurrentValue ?? ownerKey.OriginalValue);
            if (Roots().FirstOrDefault(r => string.Equals(GetId(r.Entity), ownerId, StringComparison.OrdinalIgnoreCase)) is { } root)
                changedOwners.Add(root.Entity);
        }
        foreach (var entry in Roots().ToList())
        {
            var version = entry.Property(SparkDbContext.VersionProperty);
            if (entry.State == EntityState.Added)
                version.CurrentValue = 1L;
            else if (entry.State == EntityState.Modified || (entry.State == EntityState.Unchanged && changedOwners.Contains(entry.Entity)))
                version.CurrentValue = (long)version.OriginalValue! + 1;
        }
    }

    // ---- S6: expiry, streams, reservations, set-based writes, appends (#536 M4b) ----

    /// <summary>The row's <see cref="SparkDbContext.ExpiresProperty"/> column, in UTC; a change to it alone is a write.</summary>
    public void SetExpiry(object entity, DateTimeOffset? expiresAt)
        => ExpiryOf(entity).CurrentValue = expiresAt?.ToUniversalTime();

    public DateTimeOffset? GetExpiry(object entity) => (DateTimeOffset?)ExpiryOf(entity).CurrentValue;

    private PropertyEntry ExpiryOf(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return TrackedEntry(entity) is { State: not EntityState.Detached } entry
            ? entry.Property(SparkDbContext.ExpiresProperty)
            : throw new InvalidOperationException("The session does not track this entity: load or store it first.");
    }

    /// <summary>
    /// Every row of <paramref name="query"/>, untracked, with its version. The rows are read before the first is yielded:
    /// one connection cannot read a result and the join tables at once.
    /// </summary>
    public async IAsyncEnumerable<SparkStreamResult<T>> StreamAsync<T>(IQueryable<T> query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Request();
        var x = Expression.Parameter(typeof(T), "x");
        var version = Expression.Call(typeof(EF), nameof(EF.Property), [typeof(long)], x, Expression.Constant(SparkDbContext.VersionProperty));
        var pair = Expression.New(typeof(KeyValuePair<T, long>).GetConstructor([typeof(T), typeof(long)])!, x, version);
        var rewritten = JoinRewriter.Rewrite(db, query);
        var untracked = rewritten.Provider.CreateQuery<T>(Expression.Call(AsNoTrackingMethod.MakeGenericMethod(typeof(T)), rewritten.Expression));
        var withVersions = untracked
            .Select(Expression.Lambda<Func<T, KeyValuePair<T, long>>>(pair, x));
        var rows = await withVersions.ToListAsync(cancellationToken);
        quiet = true;
        try
        {
            await HydrateAsync(rows.Select(r => (object)r.Key!), cancellationToken);
        }
        finally
        {
            quiet = false;
        }
        foreach (var (document, stored) in rows)
            yield return new SparkStreamResult<T>(SparkIdValues.ReadEntityId(document!)?.ToLowerInvariant(), document, FormatVersion(stored));
    }

    private static readonly MethodInfo AsNoTrackingMethod = typeof(EntityFrameworkQueryableExtensions).GetMethod(nameof(EntityFrameworkQueryableExtensions.AsNoTracking))!;

    private readonly List<(string Key, string OwnerId, bool Claim)> reservations = [];

    public void Reserve(string key, string ownerId) => reservations.Add((key, ownerId.ToLowerInvariant(), true));

    public void Release(string key, string ownerId) => reservations.Add((key, ownerId.ToLowerInvariant(), false));

    public async Task<string?> GetReservationAsync(string key, CancellationToken cancellationToken = default)
    {
        Request();
        return await db.Set<SparkReservationRow>().AsNoTracking().Where(r => r.Key == key).Select(r => r.OwnerId).FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Claims and releases, in the save's transaction. A key held by another document that still exists (and is not deleted
    /// by this save) is taken; one whose holder is gone is taken over. A concurrent claim fails the insert or the
    /// version check of the row, and so the save.
    /// </summary>
    private async Task ApplyReservationsAsync(CancellationToken cancellationToken)
    {
        foreach (var (key, ownerId, claim) in reservations)
        {
            var row = await db.Set<SparkReservationRow>().FindAsync([key], cancellationToken);
            if (!claim)
            {
                if (row is not null && string.Equals(row.OwnerId, ownerId, StringComparison.OrdinalIgnoreCase))
                    db.Remove(row);
                continue;
            }
            if (row is null)
            {
                db.Add(new SparkReservationRow { Key = key, OwnerId = ownerId, Version = 1 });
                continue;
            }
            var releasedHere = db.Entry(row).State == EntityState.Deleted;
            if (!releasedHere && string.Equals(row.OwnerId, ownerId, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!releasedHere && await HolderExistsAsync(row.OwnerId, cancellationToken))
                throw new SparkReservationTakenException(key, row.OwnerId);
            db.Entry(row).State = EntityState.Unchanged;
            row.OwnerId = ownerId;
            row.Version++;
        }
    }

    private async Task<bool> HolderExistsAsync(string holderId, CancellationToken cancellationToken)
    {
        var lowered = holderId.ToLowerInvariant();
        if (Roots().Any(e => string.Equals(GetId(e.Entity), lowered, StringComparison.Ordinal)
                && e.State == EntityState.Deleted))
            return false;
        var table = Model.Tables.Where(t => lowered.StartsWith(t.Prefix, StringComparison.Ordinal) || t.Key == SparkKeyShape.Text && t.KeyValue(lowered) is not null)
            .OrderByDescending(t => lowered.StartsWith(t.Prefix, StringComparison.Ordinal))
            .FirstOrDefault();
        return table is not null && (await VersionsOfAsync(table.Root, [lowered], cancellationToken)).Count > 0;
    }

    /// <summary>
    /// <c>DELETE … WHERE predicate</c>, now and outside the unit of work: no hooks, and tracked copies go stale. Child and
    /// join rows go by <c>ON DELETE CASCADE</c>; a row another row still references fails as a validation error (D30).
    /// </summary>
    public async Task<long> DeleteWhereAsync<T>(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default) where T : class
    {
        Model.RequireTable(typeof(T));
        Request();
        try
        {
            // Outside the unit of work, in a context of its own: each deleted row leaves a tombstone (D6).
            await using var own = store.NewContext();
            return await RelationalTombstones.DeleteAsync<T>(own, q => JoinRewriter.Rewrite(own, q.Where(predicate)), DateTimeOffset.UtcNow, cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            throw SparkConstraintErrors.Translate(db, db.Setup.Dialect, ex);
        }
    }

    /// <summary>
    /// <c>UPDATE … SET m = @v, Version = Version + 1 WHERE predicate</c>, now and outside the unit of work. Top-level
    /// members only: a member inside a JSON column cannot be set in place.
    /// </summary>
    public async Task<long> UpdateWhereAsync<T>(Expression<Func<T, bool>> predicate, Action<SparkSetters<T>> set, CancellationToken cancellationToken = default) where T : class
    {
        Model.RequireTable(typeof(T));
        var setters = new SparkSetters<T>();
        set(setters);
        foreach (var setter in setters.Setters)
            if (setter.Members.Count != 1)
                throw new NotSupportedException($"UpdateWhere sets top-level members on the relational providers; '{setter.Path}' is inside a JSON column.");

        Request();
        try
        {
            return await JoinRewriter.Rewrite(db, db.Set<T>().Where(predicate)).ExecuteUpdateAsync(builder =>
            {
                foreach (var setter in setters.Setters)
                    SetPropertyMethod.MakeGenericMethod(typeof(T), setter.Members[0].PropertyType)
                        .Invoke(null, [builder, setter.Members[0], setter.Value]);
                builder.SetProperty(x => EF.Property<long>(x, SparkDbContext.VersionProperty), x => EF.Property<long>(x, SparkDbContext.VersionProperty) + 1);
            }, cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            throw SparkConstraintErrors.Translate(db, db.Setup.Dialect, ex);
        }
    }

    private static readonly MethodInfo SetPropertyMethod = typeof(RelationalSparkSession).GetMethod(nameof(SetProperty), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static void SetProperty<T, TValue>(UpdateSettersBuilder<T> builder, PropertyInfo member, object? value) where T : class
    {
        var x = Expression.Parameter(typeof(T), "x");
        builder.SetProperty(Expression.Lambda<Func<T, TValue>>(Expression.Property(x, member), x), (TValue)value!);
    }

    private readonly List<(Type Type, string Id, Func<object, object?> List, object? Item)> patches = [];

    /// <summary>
    /// Appended by <see cref="SaveChangesAsync"/> to the stored row: it locks the row, reads it again and appends, so
    /// appends from units of work that read the row at the same time all land.
    /// </summary>
    public void PatchAppend<T, TItem>(string id, Expression<Func<T, IEnumerable<TItem>>> list, TItem item) where T : class
    {
        Model.RequireTable(typeof(T));
        var getter = list.Compile();
        patches.Add((typeof(T), id, entity => getter((T)entity), item));
    }

    private async Task ApplyPatchesAsync(CancellationToken cancellationToken)
    {
        if (patches.Count == 0)
            return;
        var helper = db.GetService<Microsoft.EntityFrameworkCore.Storage.ISqlGenerationHelper>();
        foreach (var (type, id, getList, item) in patches)
        {
            var table = Model.RequireTable(type);
            var key = table.KeyValue(id) ?? throw new InvalidOperationException($"'{id}' is not an id of collection {table.Name}.");
            var entityType = db.Model.FindEntityType(table.Root)!;
            var sql = db.Setup.Dialect.LockRowSql(
                helper.DelimitIdentifier(table.Name, entityType.GetSchema()),
                helper.DelimitIdentifier(entityType.FindProperty(table.IdProperty.Name)!.GetColumnName()));
            var provider = entityType.FindProperty(table.IdProperty.Name)!.GetTypeMapping().Converter;
            await db.Database.ExecuteSqlRawAsync(sql, [provider?.ConvertToProvider(key) ?? key], cancellationToken);

            object entity;
            if (TrackedById(type, id) is { } tracked)
            {
                if (tracked.State != EntityState.Unchanged || OwnedEntries(GetId(tracked.Entity)).Any(e => e.State != EntityState.Unchanged))
                    throw new InvalidOperationException($"PatchAppend on '{id}', which this session has changed: append to the list instead.");
                await tracked.ReloadAsync(cancellationToken);
                entity = tracked.Entity;
            }
            else
                entity = await LoadAsync(type, id, cancellationToken)
                    ?? throw new SparkStoreConcurrencyException($"'{id}' does not exist: there is nothing to append to.");

            if (getList(entity) is not System.Collections.IList target)
                throw new NotSupportedException($"PatchAppend needs a list on {type.Name}.");
            target.Add(item);
        }
        db.ChangeTracker.DetectChanges();
    }

    // ---- the identity map ----

    public string? GetId(object entity) => SparkIdValues.ReadEntityId(Resolve(entity))?.ToLowerInvariant();

    public string GetCollectionName(Type type) => Model.TableOf(type)?.Name ?? SparkCollectionNames.Of(type);

    public string? GetStoredCollectionName(object entity) => Model.TableOf(entity.GetType())?.Name;

    public bool IsLoaded(string id) => Roots().Any(e => string.Equals(GetId(e.Entity), id, StringComparison.OrdinalIgnoreCase));

    public bool HasChanged(object entity)
    {
        db.ChangeTracker.DetectChanges();
        var target = Resolve(entity);
        var entry = db.Entry(target);
        return entry.State != EntityState.Unchanged
            || OwnedEntries(GetId(target)).Any(e => e.State != EntityState.Unchanged);
    }

    public IReadOnlyCollection<SparkTrackedEntity> TrackedEntities
        => [.. Roots().Select(e => new SparkTrackedEntity(e.Entity, e.State == EntityState.Deleted))];

    /// <summary>Detaches the aggregate: the row and its child rows.</summary>
    public void Evict(object entity)
    {
        var target = Resolve(entity);
        foreach (var owned in OwnedEntries(GetId(target)).ToList())
            owned.State = EntityState.Detached;
        db.Entry(target).State = EntityState.Detached;
        expectations.Remove(target);
        unversionedDeletes.Remove(target);
    }

    public void Clear()
    {
        db.ChangeTracker.Clear();
        expectations.Clear();
        unversionedDeletes.Clear();
        aliases.Clear();
        patches.Clear();
        reservations.Clear();
    }

    public void Dispose() => db.Dispose();

    // ---- helpers ----

    private object Resolve(object entity) => aliases.TryGetValue(entity, out var loaded) ? loaded : entity;

    private IEnumerable<EntityEntry> Roots()
        => db.ChangeTracker.Entries().Where(e => !e.Metadata.IsOwned() && !e.Metadata.HasSharedClrType && Model.TableOf(e.Metadata.ClrType) is not null);

    private IEnumerable<EntityEntry> OwnedEntries(string? ownerId)
        => db.ChangeTracker.Entries().Where(e => e.Metadata.IsOwned()
            && string.Equals(SparkIdValues.ToText(e.Property(SparkDbContext.OwnerIdProperty).CurrentValue), ownerId, StringComparison.OrdinalIgnoreCase));

    private EntityEntry? TrackedEntry(object entity)
    {
        var target = Resolve(entity);
        return Model.TableOf(target.GetType()) is null ? null : db.Entry(target);
    }

    private EntityEntry? TrackedById(Type entityType, string id)
    {
        var table = Model.RequireTable(entityType);
        return Roots().FirstOrDefault(e => table.Root.IsInstanceOfType(e.Entity) && string.Equals(GetId(e.Entity), id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Fills the reference lists (D26) of materialized owners: one query per join table.</summary>
    internal async Task HydrateAsync(IEnumerable<object> rows, CancellationToken cancellationToken)
    {
        var list = rows.ToList();
        foreach (var join in Model.Joins)
        {
            var owners = list.Where(join.Owner.IsInstanceOfType).ToList();
            if (owners.Count == 0)
                continue;
            var owner = Model.RequireTable(join.Owner);
            var links = await db.Set<Dictionary<string, object>>(join.Name).AsNoTracking()
                .Where(JoinOwnerIn(owner, [.. owners.Select(o => owner.IdProperty.GetValue(o)!)]))
                .ToListAsync(cancellationToken);
            var byOwner = links.ToLookup(l => SparkIdValues.ToText(l[SparkDbContext.OwnerIdProperty])!, l => SparkIdValues.ToText(l[SparkDbContext.TargetIdProperty])!, StringComparer.OrdinalIgnoreCase);
            foreach (var o in owners)
                join.Property.SetValue(o, SparkIdValues.ToCollection(join.Property.PropertyType, byOwner[GetId(o)!].Order(StringComparer.Ordinal)));
        }
    }

    /// <summary><c>x =&gt; keys.Contains(x.Id)</c>, the list a captured field so EF sends it as a parameter.</summary>
    internal static IQueryable<T> WhereKeyIn<T>(IQueryable<T> source, SparkTable table, IReadOnlyCollection<object> keys)
    {
        var x = Expression.Parameter(typeof(T), "x");
        var member = Expression.Property(Expression.Convert(x, table.Root), table.IdProperty);
        var contains = Captured.Contains(table.IdProperty.PropertyType, keys, member);
        return source.Where(Expression.Lambda<Func<T, bool>>(contains, x));
    }

    private static Expression<Func<Dictionary<string, object>, bool>> JoinOwnerIn(SparkTable owner, IReadOnlyCollection<object> keys)
    {
        var r = Expression.Parameter(typeof(Dictionary<string, object>), "r");
        var property = Expression.Call(typeof(EF), nameof(EF.Property), [owner.IdProperty.PropertyType], r, Expression.Constant(SparkDbContext.OwnerIdProperty));
        return Expression.Lambda<Func<Dictionary<string, object>, bool>>(Captured.Contains(owner.IdProperty.PropertyType, keys, property), r);
    }
}

/// <summary>A value a query captures, so EF parameterizes it instead of writing it as a literal.</summary>
internal static class Captured
{
    /// <summary><c>values.Contains(member)</c> over a typed list held in a captured field.</summary>
    public static Expression Contains(Type elementType, IEnumerable<object> values, Expression member)
    {
        var list = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;
        foreach (var value in values)
            list.Add(value);
        var holder = Activator.CreateInstance(typeof(Holder<>).MakeGenericType(list.GetType()), list)!;
        var field = Expression.Field(Expression.Constant(holder), "Value");
        return Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), [elementType], field, member);
    }

    public static Expression Value(object? value, Type type)
    {
        var holder = Activator.CreateInstance(typeof(Holder<>).MakeGenericType(type), value)!;
        return Expression.Field(Expression.Constant(holder), "Value");
    }

    private sealed class Holder<T>(T value)
    {
        public readonly T Value = value;
    }
}
