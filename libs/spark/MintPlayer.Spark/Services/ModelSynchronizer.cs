using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Model;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Services.Breadcrumb;
using Raven.Client.Documents.Linq;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MintPlayer.Spark.Services;

public interface IModelSynchronizer
{
    void SynchronizeModels(SparkContext sparkContext);
}

// Deliberately NOT [Register]-ed. That attribute is harvested unconditionally into the generated
// AddSparkServices(), which put IModelSynchronizer in the container of every app in every
// environment — so production code could resolve it and drive a model rewrite, bypassing the
// environment guard that lived one layer up in the extension method. AddSparkCore now registers it
// only in Development; the build-time command constructs it directly and needs no registration.
internal partial class ModelSynchronizer : IModelSynchronizer
{
    [Inject] private readonly IHostEnvironment hostEnvironment;
    [Inject] private readonly IIndexRegistry indexRegistry;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public void SynchronizeModels(SparkContext sparkContext)
    {
        var contextType = sparkContext.GetType();
        var modelPath = Path.Combine(hostEnvironment.ContentRootPath, "App_Data", "Model");

        // Ensure directory exists
        Directory.CreateDirectory(modelPath);

        // Load existing entity types and their inline queries
        var (existingEntityTypes, existingQueries) = LoadExistingEntityTypeFiles(modelPath);

        // Find all IRavenQueryable<T> properties on the SparkContext
        var queryableProperties = contextType.GetCachedProperties()
            .Where(p => IsRavenQueryable(p.PropertyType))
            .ToList();

        // Build mapping from entity CLR type → query name for auto-resolving reference queries
        // e.g. "HR.Entities.Company" → "GetCompanies"
        var entityTypeToQueryName = new Dictionary<string, string>();
        foreach (var prop in queryableProperties)
        {
            var et = GetQueryableEntityType(prop.PropertyType);
            if (et == null) continue;
            if (indexRegistry.IsProjectionType(et)) continue;
            var clrTypeName = et.FullName ?? et.Name;
            entityTypeToQueryName[clrTypeName] = $"Get{prop.Name}";
        }

        // Track types to process (including embedded types)
        var processedTypes = new HashSet<string>();
        // Paths written during this run. The stale-projection cleanup below keys off it so it can
        // never delete a file this same run produced.
        var writtenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var typesToProcess = new Queue<Type>();

        // Grouped by entity type, because a context may expose the same type more than once
        // (e.g. Cars and ArchivedCars, both IRavenQueryable<Car>). Both map to the same
        // {TypeName}.json, and writing per property meant the second write dropped the query the
        // first had just added — from a snapshot of the directory taken once, before any write.
        //
        // The result was stable rather than noisy, which is what made it dangerous: the file
        // converged from run 1 onward on the LAST property's query alone, byte-identical every run,
        // with the same model hash. The earlier property's query was minted fresh in memory each
        // time, logged as created, and overwritten before it ever reached disk. Nothing that
        // compares runs — the idempotency guard, a regenerate-and-diff gate, the verify gate — can
        // see a wrong answer that never changes.
        //
        // One file, one write, one query per property.
        var rootsByEntityType = queryableProperties
            .Select(property => new { Property = property, EntityType = GetQueryableEntityType(property.PropertyType) })
            .Where(x => x.EntityType is not null)
            .Where(x =>
            {
                // Projection types are merged into their collection type's JSON file
                if (!indexRegistry.IsProjectionType(x.EntityType!)) return true;
                Console.WriteLine($"Skipping projection type: {x.EntityType!.Name} (merged into collection type)");
                return false;
            })
            .GroupBy(x => x.EntityType!);

        foreach (var group in rootsByEntityType)
        {
            var entityType = group.Key;
            var clrType = entityType.FullName ?? entityType.Name;

            // Get projection type from IndexRegistry (populated from FromIndexAttribute on projections)
            var registration = indexRegistry.GetRegistrationForCollectionType(entityType);
            Type? projectionType = registration?.ProjectionType;
            string? indexName = registration?.IndexName;

            // Find or create entity type definition (merging with projection type if present)
            var existingDef = existingEntityTypes.Values.FirstOrDefault(e => e.ClrType == clrType);
            var entityTypeDef = CreateOrUpdateEntityTypeDefinition(entityType, projectionType, indexName, existingDef, entityTypeToQueryName);

            // Collect existing inline queries for this entity type, plus create default if missing
            var queriesForType = CollectQueriesFor(existingQueries, entityType.Name);

            // #276 pre-pass, before the mint loop: a renamed context property otherwise leaves the
            // old query behind with a dead "Database.OldName" source (silently returning no rows)
            // AND mints a duplicate for the new name. Exactly one dead Database.* source plus
            // exactly one unclaimed property is a rename with high confidence — retarget the
            // existing query in place, preserving its Id (program units reference queries by id)
            // and all authoring. Anything else is ambiguous: warn and keep, never guess, never
            // delete. Custom.* queries and indexName/useProjection are never touched — the
            // synchronizer never wrote them, so every value is authored.
            var propertyNames = group.Select(x => x.Property.Name).ToList();
            RetargetRenamedDatabaseQueries(queriesForType, propertyNames, entityType.Name);

            // One default query per context property exposing this type.
            foreach (var property in group.Select(x => x.Property))
            {
                var queryName = $"Get{property.Name}";
                if (queriesForType.Any(q => q.Name == queryName))
                    continue;

                queriesForType.Add(new SparkQuery
                {
                    Id = Guid.NewGuid(),
                    Name = queryName,
                    // Set eagerly, though LoadExistingEntityTypeFiles would derive the same value on
                    // the next read. Leaving it null made synchronization non-idempotent: the first
                    // run omitted the field, the second read it back, populated it and wrote it out,
                    // so two consecutive runs produced different bytes.
                    EntityType = entityType.Name,
                    Source = $"Database.{property.Name}",
                    SortColumns = GetDefaultSortProperty(entityTypeDef) is string sortProp
                        ? [new SortColumn { Property = sortProp, Direction = "asc" }]
                        : []
                });
                Console.WriteLine($"Created query: {queryName} (inline in {entityType.Name}.json)");
            }

            // Save the entity type file with inline queries
            var fileName = Path.Combine(modelPath, $"{entityType.Name}.json");
            var entityTypeFile = new EntityTypeFile
            {
                PersistentObject = entityTypeDef,
                // Name-sorted for the same reason as the attributes: stable across runs and
                // merge-friendly. Nothing depends on the order of this array.
                Queries = [.. queriesForType
                    .OrderBy(q => q.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(q => q.Name, StringComparer.Ordinal)]
            };
            var json = JsonSerializer.Serialize(entityTypeFile, JsonOptions);
            File.WriteAllText(fileName, json);
            writtenFiles.Add(fileName);
            processedTypes.Add(clrType);

            // Also mark projection type as processed (no separate JSON file)
            if (projectionType != null)
            {
                var projectionClrType = projectionType.FullName ?? projectionType.Name;
                processedTypes.Add(projectionClrType);
                Console.WriteLine($"Synchronized model: {entityType.Name} (merged with {projectionType.Name}) -> {fileName}");
            }
            else
            {
                Console.WriteLine($"Synchronized model: {entityType.Name} -> {fileName}");
            }

            // Collect embedded types from this entity
            CollectEmbeddedTypes(entityType, typesToProcess, processedTypes);

            // Also collect embedded types from projection type (if any)
            if (projectionType != null)
            {
                CollectEmbeddedTypes(projectionType, typesToProcess, processedTypes);
            }
        }

        // Process embedded types
        while (typesToProcess.Count > 0)
        {
            var embeddedType = typesToProcess.Dequeue();
            var clrType = embeddedType.FullName ?? embeddedType.Name;

            if (processedTypes.Contains(clrType))
                continue;

            var existingDef = existingEntityTypes.Values.FirstOrDefault(e => e.ClrType == clrType);
            var entityTypeDef = CreateOrUpdateEntityTypeDefinition(embeddedType, projectionType: null, indexName: null, existingDef, entityTypeToQueryName);

            // Preserve any existing inline queries for this embedded type
            var embeddedQueries = CollectQueriesFor(existingQueries, embeddedType.Name).ToArray();

            var fileName = Path.Combine(modelPath, $"{embeddedType.Name}.json");
            var entityTypeFile = new EntityTypeFile
            {
                PersistentObject = entityTypeDef,
                Queries = [.. embeddedQueries
                    .OrderBy(q => q.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(q => q.Name, StringComparer.Ordinal)]
            };
            var json = JsonSerializer.Serialize(entityTypeFile, JsonOptions);
            File.WriteAllText(fileName, json);
            writtenFiles.Add(fileName);
            processedTypes.Add(clrType);

            Console.WriteLine($"Synchronized model (embedded): {embeddedType.Name} -> {fileName}");

            // Recursively collect embedded types from this type
            CollectEmbeddedTypes(embeddedType, typesToProcess, processedTypes);
        }

        // Clean up model files left behind by an older version that gave projection types their own
        // file. Projection types are merged into their collection type's file now, so nothing here
        // can create one.
        //
        // Skipping anything written during this run is what makes it safe. Model files are keyed by
        // SIMPLE type name, so a real entity sharing a projection's simple name resolves to the same
        // path — and this loop runs after every write, so it used to delete a file the same run had
        // just produced and report success. Matching on the full type name is not an option: the
        // path carries only the simple name.
        foreach (var registration in indexRegistry.GetAllRegistrations())
        {
            if (registration.ProjectionType == null)
                continue;

            var staleModelFile = Path.Combine(modelPath, $"{registration.ProjectionType.Name}.json");
            if (writtenFiles.Contains(staleModelFile) || !File.Exists(staleModelFile))
                continue;

            File.Delete(staleModelFile);
            Console.WriteLine($"Removed stale projection model file: {registration.ProjectionType.Name}.json");
        }

        WriteModelHashes(contextType);
    }

    /// <summary>
    /// Records the fingerprint of the entity classes these model files were generated from, so a
    /// deployed application can tell that its model no longer describes its classes.
    /// </summary>
    private void WriteModelHashes(Type contextType)
    {
        // Computed after every model file has been written, so the file hash covers the output of
        // this same run.
        var hashFile = BuildModelHashes(contextType, indexRegistry, hostEnvironment.ContentRootPath);
        hashFile.Write(hostEnvironment.ContentRootPath);

        Console.WriteLine($"Model hash: {hashFile.ModelHash} -> {ModelHashFile.PathFor(hostEnvironment.ContentRootPath)}");
    }

    /// <summary>
    /// Computes the hash file for a context type. Shared with the startup check so the value written
    /// and the value verified can never be produced by two different pieces of code.
    /// </summary>
    internal static ModelHashFile BuildModelHashes(Type contextType, IIndexRegistry indexRegistry, string contentRootPath)
    {
        var shapes = ModelShapeDiscovery.Discover(contextType, indexRegistry);
        var perEntity = SparkModelShape.ComputePerEntityHashes(shapes);
        var contextRoots = SparkModelShape.ComputeContextRootsHash(
            ModelShapeDiscovery.RootEntityNames(contextType, indexRegistry));
        var fileHashes = ModelHashFile.ComputeFileHashes(contentRootPath);
        var modelFiles = ModelHashFile.CombineFileHashes(fileHashes);

        return new ModelHashFile
        {
            ModelHash = SparkModelShape.ComputeModelHash(perEntity, contextRoots, modelFiles),
            ContextRoots = contextRoots,
            ModelFiles = modelFiles,
            Files = fileHashes,
            Entities = new SortedDictionary<string, string>(perEntity.ToDictionary(e => e.Key, e => e.Value), StringComparer.Ordinal),
        };
    }

    private void CollectEmbeddedTypes(Type entityType, Queue<Type> typesToProcess, HashSet<string> processedTypes)
    {
        // Shares the model-property filter with CreateOrUpdateEntityTypeDefinition on purpose:
        // discovery and attribute generation must agree, or an ignored complex property would
        // still get its type written out as an embedded model file that nothing references.
        var properties = entityType.GetSparkModelProperties();

        foreach (var property in properties)
        {
            var propType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

            // Unwrap array/collection element type
            var elementType = GetCollectionElementType(propType);
            if (elementType != null)
            {
                propType = elementType;
            }

            var clrType = propType.FullName ?? propType.Name;

            if (IsComplexType(propType) && !processedTypes.Contains(clrType))
            {
                typesToProcess.Enqueue(propType);
            }
        }
    }

    /// <summary>
    /// Inline queries belonging to <paramref name="entityTypeName"/>, de-duplicated by id.
    ///
    /// <para>
    /// The de-duplication is load-bearing, not defensive. <c>existingQueries</c> is the flat
    /// concatenation of the inline queries of <em>every</em> model file, and a query is written into
    /// the file of the entity it names. Normally that is self-correcting: a query sitting in the
    /// wrong file is copied to the right one, and the wrong file is rewritten without it.
    /// </para>
    ///
    /// <para>
    /// It stops being self-correcting when a model file is never rewritten — an orphan whose type is
    /// no longer a context root nor a reachable embedded type, which is what you get by removing or
    /// renaming an entity without deleting its JSON. Its copy is re-read every run and appended
    /// again, so the live entity's file grows by one query per synchronize, without bound. Measured
    /// at +1 query and +379 bytes per run before this guard.
    /// </para>
    ///
    /// <para>
    /// Two entries with the same id are the same query, so keeping the first is always correct.
    /// Entries that merely share a <c>Name</c> are left alone: that is an ambiguous model worth
    /// surfacing rather than silently resolving.
    /// </para>
    /// </summary>
    private static List<SparkQuery> CollectQueriesFor(List<SparkQuery> existingQueries, string entityTypeName)
    {
        var seenIds = new HashSet<Guid>();
        var result = new List<SparkQuery>();

        foreach (var query in existingQueries)
        {
            if (!string.Equals(query.EntityType, entityTypeName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (seenIds.Add(query.Id))
                result.Add(query);
        }

        return result;
    }

    private (Dictionary<Guid, EntityTypeDefinition> EntityTypes, List<SparkQuery> Queries) LoadExistingEntityTypeFiles(string modelPath)
    {
        var entityTypes = new Dictionary<Guid, EntityTypeDefinition>();
        var queries = new List<SparkQuery>();

        if (!Directory.Exists(modelPath))
            return (entityTypes, queries);

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        foreach (var file in Directory.GetFiles(modelPath, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var entityTypeFile = JsonSerializer.Deserialize<EntityTypeFile>(json, jsonOptions);
                if (entityTypeFile?.PersistentObject != null)
                {
                    var entityType = entityTypeFile.PersistentObject;
                    entityTypes[entityType.Id] = entityType;

                    // Extract inline queries, auto-populating EntityType from the containing file
                    foreach (var query in entityTypeFile.Queries)
                    {
                        query.EntityType ??= entityType.Name;
                        queries.Add(query);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading model file {file}: {ex.Message}");
            }
        }

        return (entityTypes, queries);
    }

    private EntityTypeDefinition CreateOrUpdateEntityTypeDefinition(Type entityType, Type? projectionType, string? indexName, EntityTypeDefinition? existing, Dictionary<string, string>? entityTypeToQueryName = null)
    {
        var entityTypeDef = existing ?? new EntityTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = entityType.Name,
            ClrType = entityType.FullName ?? entityType.Name,
        };

        // Update basic info
        entityTypeDef.Name = entityType.Name;
        entityTypeDef.ClrType = entityType.FullName ?? entityType.Name;

        // Preserve manually-defined tabs and groups
        entityTypeDef.Tabs = existing?.Tabs ?? [];
        entityTypeDef.Groups = existing?.Groups ?? [];

        // Assigned unconditionally, including back to null when no projection is registered.
        // Only setting them left a model pointing at a projection type that had since been deleted,
        // and because both feed the structural hash, verification would confirm the dead reference
        // instead of catching it. Clearing is consistent with the runtime rather than destructive:
        // synchronization and the running app populate the index registry from the same entry
        // assembly, so a projection missing here is missing there too.
        var resolvedQueryType = projectionType?.FullName ?? projectionType?.Name;
        if (projectionType is null && entityTypeDef.QueryType is not null)
        {
            // Deliberately does not tell the operator to add [FromIndex]: the likeliest cause is that
            // the attribute IS present and correct, on a projection that lives outside the entry
            // assembly. Index and projection discovery scan only the entry assembly, so a
            // library-shipped projection is invisible here and at runtime alike.
            Console.WriteLine(
                $"Cleared queryType '{entityTypeDef.QueryType}' on '{entityTypeDef.Name}': no projection is registered for it. " +
                "Either the projection type was removed, or it lives outside the entry assembly — " +
                "index and projection discovery only scan the entry assembly, so such a projection is " +
                "invisible to both synchronization and the running application.");
        }

        entityTypeDef.QueryType = resolvedQueryType;
        entityTypeDef.IndexName = projectionType != null ? indexName : null;

        // Get existing attributes as a dictionary for quick lookup
        // Reported rather than left to ToDictionary, which throws "An item with the same key has
        // already been added" and names neither the entity nor the file — a duplicate attribute in a
        // hand-edited model would kill the command with nothing to act on.
        var duplicateAttributeName = entityTypeDef.Attributes
            .GroupBy(a => a.Name, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1)?.Key;

        if (duplicateAttributeName is not null)
        {
            throw new InvalidOperationException(
                $"Model for '{entityTypeDef.Name}' declares the attribute '{duplicateAttributeName}' more than once. " +
                $"Attribute names must be unique within a persistent object — remove the duplicate from " +
                $"App_Data/Model/{entityTypeDef.Name}.json.");
        }

        var existingAttrs = entityTypeDef.Attributes.ToDictionary(a => a.Name, a => a);

        // Get properties from collection type
        var collectionProperties = entityType.GetSparkModelProperties()
            .ToDictionary(p => p.Name, p => p);

        // Get properties from projection type (if any)
        var projectionProperties = projectionType?.GetSparkModelProperties()
            .ToDictionary(p => p.Name, p => p)
            ?? new Dictionary<string, PropertyInfo>();

        // [IgnoreProperty] on either side vetoes the property outright. The two name sets are
        // unioned below, so filtering each side independently is not enough: ignoring a property
        // on the entity would otherwise let it back in through a projection that still declares it.
        var ignoredPropertyNames = entityType.GetCachedProperties()
            .Concat(projectionType?.GetCachedProperties() ?? [])
            .Where(p => p.IsIgnoredForSparkModel())
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        // Merge property names from both types
        var allPropertyNames = collectionProperties.Keys
            .Union(projectionProperties.Keys)
            .Distinct()
            .Where(name => !ignoredPropertyNames.Contains(name))
            .ToList();

        // Build new attributes list, preserving existing IDs and custom settings
        var newAttributes = new List<EntityAttributeDefinition>();
        var order = 1;

        foreach (var propertyName in allPropertyNames)
        {
            var inCollectionType = collectionProperties.TryGetValue(propertyName, out var collectionProp);
            var inQueryType = projectionProperties.TryGetValue(propertyName, out var projectionProp);

            // Use collection property if available, otherwise use projection property
            var property = collectionProp ?? projectionProp!;

            // Validate type compatibility if property exists in both
            if (inCollectionType && inQueryType)
            {
                var collectionDataType = GetDataType(collectionProp!.PropertyType);
                var projectionDataType = GetDataType(projectionProp!.PropertyType);

                if (!AreDataTypesCompatible(collectionDataType, projectionDataType))
                {
                    throw new InvalidOperationException(
                        $"Type mismatch for property '{propertyName}' between collection type '{entityType.Name}' " +
                        $"({collectionProp!.PropertyType.Name} -> {collectionDataType}) and projection type '{projectionType!.Name}' " +
                        $"({projectionProp!.PropertyType.Name} -> {projectionDataType}). Property types must be convertible.");
                }
            }

            var referenceAttr = property.GetCachedCustomAttribute<ReferenceAttribute>();
            var lookupRefAttr = property.GetCachedCustomAttribute<LookupReferenceAttribute>();
            var sortableAttr = property.GetCachedCustomAttribute<SortableAttribute>();
            var propType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            var dataType = referenceAttr != null ? "Reference" : GetDataType(property.PropertyType);
            string? referenceType = referenceAttr?.TargetType.FullName ?? referenceAttr?.TargetType.Name;

            // For AsDetail, resolve the actual element type (unwrap arrays/collections).
            // For non-AsDetail collections (scalar arrays, or [Reference] List<string>),
            // mark IsArray so the wire value round-trips as a JSON array of ids/scalars.
            var isArray = false;
            string? asDetailType = null;
            var collectionElementType = GetCollectionElementType(propType);
            if (dataType == "AsDetail")
            {
                if (collectionElementType != null)
                {
                    isArray = true;
                    asDetailType = collectionElementType.FullName ?? collectionElementType.Name;
                }
                else
                {
                    asDetailType = propType.FullName ?? propType.Name;
                }
            }
            else if (collectionElementType != null)
            {
                // e.g. [Reference(typeof(Tag), "GetTags")] List<string> TagIds, or a bare
                // List<string> of scalars — a real array of simple/reference values.
                isArray = true;
            }

            string? lookupReferenceType = lookupRefAttr?.LookupType.Name;

            // [Sortable] only takes effect on AsDetail arrays. Derived purely from the CLR
            // shape (like IsArray), so it's always refreshed. Null (not false) for the
            // non-sortable case keeps the flag absent from the model JSON.
            bool? isSortable = sortableAttr != null && dataType == "AsDetail" && isArray ? true : null;

            // Auto-resolve query name for reference attributes when not explicitly specified
            string? resolvedQuery = referenceAttr?.Query;
            if (resolvedQuery == null && referenceAttr != null && entityTypeToQueryName != null)
            {
                var targetClrType = referenceAttr.TargetType.FullName ?? referenceAttr.TargetType.Name;
                entityTypeToQueryName.TryGetValue(targetClrType, out resolvedQuery);
            }

            // Determine ShowedOn based on inQueryType/inCollectionType
            // If property doesn't exist in projection type (inQueryType=false), only show on PersistentObject pages
            // If property doesn't exist in collection type (inCollectionType=false), only show on Query pages
            EShowedOn showedOn = EShowedOn.Query | EShowedOn.PersistentObject;
            if (projectionType != null)
            {
                if (!inQueryType && inCollectionType)
                {
                    // Property only in collection type - show only on detail/edit pages
                    showedOn = EShowedOn.PersistentObject;
                }
                else if (inQueryType && !inCollectionType)
                {
                    // Property only in projection type - show only on query/list pages
                    showedOn = EShowedOn.Query;
                }
            }

            if (existingAttrs.TryGetValue(propertyName, out var existingAttr))
            {
                // Captured before the overwrites below: whether the stored attribute WAS a
                // reference is the provenance signal that decides the Query assignment (#275).
                var wasReference = existingAttr.DataType == "Reference" || existingAttr.ReferenceType != null;

                // Update existing attribute, preserving custom settings.
                // "MultiLineString" is a presentation-only override of a string property (render a
                // textarea instead of a single-line input): the CLR shape is still string, so keep a
                // hand-set MultiLineString rather than resetting it to "string" on every sync. Any other
                // change still wins - switching the property away from string clears it.
                if (!(existingAttr.DataType == "MultiLineString" && dataType == "string"))
                {
                    existingAttr.DataType = dataType;
                }
                existingAttr.Order = existingAttr.Order > 0 ? existingAttr.Order : order;

                // Assigned unconditionally, including back to null. These are all derived from the
                // CLR shape, so leaving a stale value behind when the attribute or type changes
                // persists a reference to something that no longer exists — and because they are
                // part of the structural hash, verification would then confirm the dead reference
                // rather than catch it.
                existingAttr.ReferenceType = referenceAttr != null ? referenceType : null;

                // Query is provenance-gated (#275): a derived value only exists when the property
                // carries [Reference] — re-derive it then; clear it when the reference was removed
                // (the stored value was machine-derived and is now stale); otherwise the stored
                // value could only have been authored — preserve it.
                if (referenceAttr != null)
                {
                    existingAttr.Query = resolvedQuery;
                }
                else if (wasReference)
                {
                    if (existingAttr.Query != null)
                        Console.WriteLine(
                            $"  Cleared query '{existingAttr.Query}' on attribute '{propertyName}': " +
                            $"it was derived from a [Reference] that no longer exists.");
                    existingAttr.Query = null;
                }

                // IsArray is derived purely from the CLR property shape, so always
                // refresh it (covers Reference/scalar arrays, not just AsDetail).
                existingAttr.IsArray = isArray;
                existingAttr.IsSortable = isSortable;

                existingAttr.AsDetailType = dataType == "AsDetail" ? asDetailType : null;
                existingAttr.LookupReferenceType = lookupRefAttr != null ? lookupReferenceType : null;

                // Set InCollectionType/InQueryType flags only when projection type exists
                if (projectionType != null)
                {
                    existingAttr.InCollectionType = inCollectionType ? null : false;
                    existingAttr.InQueryType = inQueryType ? null : false;
                    // ShowedOn is presentation constrained by structure: projection/entity
                    // membership is the capability to appear on a side, the model author picks the
                    // subset. Strip sides that structurally disappeared, never re-add one (#274).
                    // An empty result self-heals to the derived capability.
                    var narrowedShowedOn = existingAttr.ShowedOn & showedOn;
                    existingAttr.ShowedOn = narrowedShowedOn != 0 ? narrowedShowedOn : showedOn;
                }
                else
                {
                    existingAttr.InCollectionType = null;
                    existingAttr.InQueryType = null;
                }

                newAttributes.Add(existingAttr);
            }
            else
            {
                // Create new attribute
                var newAttr = new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(),
                    Name = propertyName,
                    Label = TranslatedString.Create(AddSpacesToCamelCase(propertyName)),
                    DataType = dataType,
                    // A get-only property cannot be required: nothing can supply a value for it.
                    IsRequired = property.CanWrite
                        && !IsNullable(property.PropertyType)
                        && property.PropertyType != typeof(string),
                    IsVisible = true,
                    // Computed properties surface read-only rather than not at all. Only set on
                    // creation — the update branch never reassigns IsReadOnly, so a hand-set value
                    // survives re-synchronize.
                    IsReadOnly = !property.CanWrite,
                    Order = order,
                    Query = resolvedQuery,
                    ReferenceType = referenceType,
                    AsDetailType = asDetailType,
                    IsArray = isArray,
                    IsSortable = isSortable,
                    LookupReferenceType = lookupReferenceType,
                    // Set InCollectionType/InQueryType flags only when projection type exists
                    InCollectionType = projectionType != null ? (inCollectionType ? null : false) : null,
                    InQueryType = projectionType != null ? (inQueryType ? null : false) : null,
                    ShowedOn = showedOn,
                    Rules = []
                };
                newAttributes.Add(newAttr);
            }
            order++;
        }

        // The rebuild above is omission-based: it walks the current property set, so an attribute
        // with no matching CLR property is dropped simply by never being re-added. Carry those over.
        // Synchronize adds and modifies; it does not delete.
        //
        // Two kinds of attribute land here, and both must survive. A *virtual* attribute is authored
        // by hand and never had a property — its value is supplied at runtime. An *orphaned* one had
        // a property that was renamed or removed. Nothing in the model distinguishes them, which is
        // also why there is no --prune-orphaned-attributes flag: see docs/issue_253_PRD.md (D1).
        //
        // [IgnoreProperty] is the deliberate exception. Marking a property ignored is an explicit
        // instruction to drop its attribute, unlike a property that merely disappeared, so vetoed
        // names stay dropped.
        var rebuiltNames = newAttributes.Select(a => a.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var carriedOver in entityTypeDef.Attributes)
        {
            if (rebuiltNames.Contains(carriedOver.Name) || ignoredPropertyNames.Contains(carriedOver.Name))
                continue;

            // Added as-is, by reference: Id, Label, Rules, Renderer, RendererOptions, Group and
            // EditMode all ride along untouched. The Id matters most — clients key on it, so
            // regenerating one silently rewrites identity.
            newAttributes.Add(carriedOver);

            Console.WriteLine(
                $"Kept attribute '{carriedOver.Name}' on '{entityTypeDef.Name}': no matching CLR property. "
                + "Remove it from the model JSON if it is obsolete.");

            // ValidationService (:41) walks the MODEL's attributes and rejects a save when a
            // required one is empty, so a required attribute the mapper cannot populate blocks
            // every save of this type. Not necessarily broken — a value submitted by the client
            // satisfies it, which is plausible for a virtual attribute — so this warns rather than
            // silently clearing IsRequired. Rewriting hand-authored model state is the failure mode
            // this whole change exists to remove.
            if (carriedOver.IsRequired)
            {
                Console.WriteLine(
                    $"  WARNING: '{carriedOver.Name}' is required but has no CLR property to populate it. "
                    + "Saves will fail unless a value is supplied by the client. "
                    + "Set \"isRequired\": false in the model JSON, or remove the attribute.");
            }
        }

        // Sorted by name, deliberately not by Order. Order exists precisely so that position in this
        // array carries no meaning — every consumer sorts by it — which frees the array itself to be
        // written in the shape that merges best.
        //
        // Case-insensitive first so names group the way a reader expects, then case-sensitive as a
        // tiebreaker: OrdinalIgnoreCase alone is not a total order, so two names differing only in
        // case would compare equal and a stable sort would fall back to reflection order — quietly
        // restoring the instability this exists to remove.
        //
        // Two payoffs. Reflection member order is not stable (swapping the files of a partial class
        // reorders GetProperties), so an unsorted array churns between builds with no source change.
        // And a stable name order means two branches adding different attributes touch different
        // lines, so they merge instead of conflicting — the same reasoning behind keeping the model
        // hashes in their own file.
        entityTypeDef.Attributes = [.. newAttributes
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Name, StringComparer.Ordinal)];

        // Breadcrumb template: the model JSON is the display authority (Vidyano-style) — an
        // authored value is preserved verbatim; only a missing one gets a synthesized default.
        // Then validate the template and flag whether it is renderable from the projection alone.
        if (string.IsNullOrEmpty(entityTypeDef.Breadcrumb))
            entityTypeDef.Breadcrumb = SynthesizeDefaultBreadcrumb(entityType, newAttributes);
        else
            WarnOnBreadcrumbMarkerDrift(entityTypeDef, entityType);

        ValidateBreadcrumb(entityTypeDef, entityType, projectionType);
        entityTypeDef.BreadcrumbProjectionSatisfiable = ComputeBreadcrumbProjectionSatisfiable(entityTypeDef, projectionType);

        return entityTypeDef;
    }

    /// <summary>
    /// Detects a renamed context property among an entity's <c>Database.*</c> queries and
    /// retargets the existing query in place. See the call-site comment for the confidence rule;
    /// dead sources that cannot be paired are kept and warned about — a transient state (a
    /// property temporarily removed, a module assembly missing) must never destroy authoring.
    /// </summary>
    private static void RetargetRenamedDatabaseQueries(
        List<SparkQuery> queriesForType, IReadOnlyList<string> propertyNames, string entityName)
    {
        const string Prefix = "Database.";

        var claimedSources = queriesForType
            .Where(q => q.Source.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            .Select(q => q.Source.Substring(Prefix.Length))
            .ToHashSet(StringComparer.Ordinal);

        var deadQueries = queriesForType
            .Where(q => q.Source.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
                && !propertyNames.Contains(q.Source.Substring(Prefix.Length), StringComparer.Ordinal))
            .ToList();
        var unclaimedProperties = propertyNames
            .Where(p => !claimedSources.Contains(p))
            .ToList();

        if (deadQueries.Count == 1 && unclaimedProperties.Count == 1)
        {
            var query = deadQueries[0];
            var oldProperty = query.Source.Substring(Prefix.Length);
            var newProperty = unclaimedProperties[0];

            query.Source = $"{Prefix}{newProperty}";

            // A conventionally-named query (and its auto-derived alias) follows the rename; an
            // unconventional name is authored and stays.
            if (query.Name == $"Get{oldProperty}")
            {
                if (query.Alias == oldProperty.ToLowerInvariant())
                    query.Alias = null;
                query.Name = $"Get{newProperty}";
            }

            Console.WriteLine(
                $"  Retargeted query '{query.Name}' from Database.{oldProperty} to " +
                $"Database.{newProperty} (renamed context property, {entityName}.json).");
            return;
        }

        foreach (var query in deadQueries)
        {
            Console.WriteLine(
                $"Warning: query '{query.Name}' in {entityName}.json sources '{query.Source}', " +
                $"which is not a property on the SparkContext. It will return no rows. " +
                $"Retarget or remove it.");
        }
    }

    /// <summary>
    /// Default breadcrumb when none is authored: the type's <c>[Breadcrumb]</c>-marked property
    /// when present — that keeps display and the generated sort companion agreeing by default —
    /// else prefer Name/FullName/Title, else the first attribute.
    /// </summary>
    private static string? SynthesizeDefaultBreadcrumb(Type entityType, IReadOnlyList<EntityAttributeDefinition> attributes)
    {
        var marked = entityType.GetBreadcrumbProperty();
        if (marked is not null)
            return $"{{{marked.Name}}}";

        var name = attributes.FirstOrDefault(a => a.Name is "Name" or "FullName" or "Title")?.Name
            ?? attributes.FirstOrDefault()?.Name;
        return name is null ? null : $"{{{name}}}";
    }

    /// <summary>
    /// An authored template that omits the type's <c>[Breadcrumb]</c>-marked property means the
    /// grid sorts a column by one string while the breadcrumb displays another — legal, but
    /// invisible to every gate (the template is presentational and unhashed), so it is warned
    /// about rather than silently accepted.
    /// </summary>
    private static void WarnOnBreadcrumbMarkerDrift(EntityTypeDefinition def, Type entityType)
    {
        var marked = entityType.GetBreadcrumbProperty();
        if (marked is null || string.IsNullOrEmpty(def.Breadcrumb)) return;
        if (def.Breadcrumb.Contains($"{{{marked.Name}}}", StringComparison.Ordinal)) return;

        Console.WriteLine(
            $"Warning: entity '{def.Name}' marks [Breadcrumb] on '{marked.Name}', but its " +
            $"breadcrumb template '{def.Breadcrumb}' does not reference it. Sorting (via the " +
            $"generated companion) and display will disagree.");
    }

    /// <summary>Fails fast on malformed templates (bad braces, unknown placeholder attribute).</summary>
    private static void ValidateBreadcrumb(EntityTypeDefinition def, Type? entityType = null, Type? projectionType = null)
    {
        if (string.IsNullOrEmpty(def.Breadcrumb)) return;

        IReadOnlyList<BreadcrumbToken> tokens;
        try
        {
            tokens = BreadcrumbTemplate.Parse(def.Breadcrumb);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                $"Invalid breadcrumb template on entity '{def.Name}': {ex.Message}", ex);
        }

        var attrNames = def.Attributes.Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var field in tokens.OfType<FieldToken>())
        {
            if (field.AttributeName == "Id") continue;
            if (attrNames.Contains(field.AttributeName)) continue;

            // The sanctioned marker shape — a [Breadcrumb] property hidden with [IgnoreProperty] —
            // is persisted and readable, so a placeholder naming it renders fine despite being
            // outside the model.
            if (IsBreadcrumbMarkedProperty(field.AttributeName, entityType, projectionType))
                continue;

            // Distinguish "no such property" from "you excluded it" — otherwise adding
            // [IgnoreProperty] to a breadcrumb field fails with a misleading "unknown attribute".
            if (IsIgnoredProperty(field.AttributeName, entityType, projectionType))
                throw new InvalidOperationException(
                    $"Breadcrumb template on entity '{def.Name}' references attribute " +
                    $"'{{{field.AttributeName}}}', which is marked [IgnoreProperty] and is " +
                    $"therefore not part of the model. Remove it from the breadcrumb template " +
                    $"or drop the [IgnoreProperty] attribute.");

            throw new InvalidOperationException(
                $"Breadcrumb template on entity '{def.Name}' references unknown attribute " +
                $"'{{{field.AttributeName}}}'. Known attributes: {string.Join(", ", attrNames)}.");
        }
    }

    private static bool IsIgnoredProperty(string name, Type? entityType, Type? projectionType)
        => (entityType?.GetCachedProperty(name)?.IsIgnoredForSparkModel() ?? false)
            || (projectionType?.GetCachedProperty(name)?.IsIgnoredForSparkModel() ?? false);

    private static bool IsBreadcrumbMarkedProperty(string name, Type? entityType, Type? projectionType)
        => entityType?.GetCachedProperty(name)?.GetCachedCustomAttribute<BreadcrumbAttribute>() is not null
            || projectionType?.GetCachedProperty(name)?.GetCachedCustomAttribute<BreadcrumbAttribute>() is not null;

    /// <summary>
    /// null = renderable from the projection (or no projection); false = a placeholder field
    /// is absent from the projection, so the list path must batch-load collection documents.
    /// </summary>
    private static bool? ComputeBreadcrumbProjectionSatisfiable(EntityTypeDefinition def, Type? projectionType)
    {
        if (projectionType is null || string.IsNullOrEmpty(def.Breadcrumb))
            return null;

        // Only the ignore check applies here — a get-only projection property can still satisfy
        // a breadcrumb field even though it is not a model attribute.
        var projectionProps = projectionType.GetCachedProperties()
            .Where(p => !p.IsIgnoredForSparkModel())
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var field in BreadcrumbTemplate.FieldNames(def.Breadcrumb))
        {
            if (field == "Id") continue;
            if (!projectionProps.Contains(field))
                return false;
        }
        return null;
    }

    private bool AreDataTypesCompatible(string type1, string type2)
    {
        // Same types are always compatible
        if (type1 == type2) return true;

        // Number and decimal are compatible (both are numeric)
        var numericTypes = new HashSet<string> { "number", "decimal" };
        if (numericTypes.Contains(type1) && numericTypes.Contains(type2)) return true;

        return false;
    }

    private bool IsRavenQueryable(Type type)
    {
        if (!type.IsGenericType) return false;
        var genericDef = type.GetGenericTypeDefinition();
        return genericDef == typeof(IRavenQueryable<>);
    }

    private Type? GetQueryableEntityType(Type queryableType)
    {
        if (!queryableType.IsGenericType) return null;
        return queryableType.GetGenericArguments().FirstOrDefault();
    }

    // Delegates to the shared shape definition so the generator and the startup hash check can
    // never disagree about what a property's data type is.
    private string GetDataType(Type type) => SparkModelShape.GetDataType(type);

    private bool IsCollectionOfComplexType(Type type)
    {
        var elementType = GetCollectionElementType(type);
        return elementType != null && IsComplexType(elementType);
    }

    private static Type? GetCollectionElementType(Type type) => SparkModelShape.GetCollectionElementType(type);

    private bool IsComplexType(Type type) => SparkModelShape.IsComplexType(type);

    private bool IsNullable(Type type) => SparkModelShape.IsNullable(type);

    private string AddSpacesToCamelCase(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var result = new System.Text.StringBuilder();
        result.Append(text[0]);

        for (int i = 1; i < text.Length; i++)
        {
            if (char.IsUpper(text[i]))
            {
                result.Append(' ');
            }
            result.Append(text[i]);
        }

        return result.ToString();
    }

    private string? GetDefaultSortProperty(EntityTypeDefinition entityType)
    {
        // Prefer Name, LastName, or first string attribute
        var sortAttr = entityType.Attributes
            .FirstOrDefault(a => a.Name is "Name" or "LastName")
            ?? entityType.Attributes.FirstOrDefault(a => a.DataType == "string");

        return sortAttr?.Name;
    }
}
