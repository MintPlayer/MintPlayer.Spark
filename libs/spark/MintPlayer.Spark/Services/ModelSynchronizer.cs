using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Model;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Configuration;
using MintPlayer.Spark.Helpers;
using MintPlayer.Spark.Layering;
using MintPlayer.Spark.Services.Breadcrumb;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Linq;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MintPlayer.Spark.Services;

public interface IModelSynchronizer
{
    void SynchronizeModels(Type contextType);
}

// Deliberately NOT [Register]-ed. That attribute is harvested unconditionally into the generated
// AddSparkServices(), which put IModelSynchronizer in the container of every app in every
// environment — so production code could resolve it and drive a model rewrite, bypassing the
// environment guard that lived one layer up in the extension method. AddSparkCore now registers it
// only in Development; the build-time command constructs it directly and needs no registration.
internal partial class ModelSynchronizer : IModelSynchronizer
{
    [Inject] private readonly IHostEnvironment hostEnvironment;
    [Inject] private readonly IIndexCatalog indexCatalog;

    /// <summary>C#-side description seeds (#348); one instance per synchronize run so each assembly is read once.</summary>
    private readonly AttributeDescriptionCatalog descriptions = new();

    /// <summary>Description seeds collected during the run, written into translations.json at its end (#467, D5).</summary>
    private readonly List<(string Key, string Text)> seeds = new();

    /// <summary>A library model type as the synchronizer compares against it (composition D6, D28).</summary>
    private sealed record LibraryModelType(string Library, string Identity, string Name, string? ClrType, SparkJsonObject Layer);

    /// <summary>The library model types by effective name, ignoring case (composition D6): never copied into the application, only their delta written.</summary>
    private Dictionary<string, LibraryModelType> libraryTypes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The library model types by CLR type (F7, D28): an application class is the library's type only when it IS
    /// the library's class. Matching by name made an application <c>Order</c> a delta on a library <c>Order</c>.
    /// </summary>
    private Dictionary<string, LibraryModelType> libraryTypesByClrType = new(StringComparer.Ordinal);

    /// <summary>The application's own CLR types named by <c>[assembly: PersistentObjectName&lt;T&gt;]</c> (D28): CLR full name → name.</summary>
    private Dictionary<string, string> applicationNames = new(StringComparer.Ordinal);

    /// <summary>The application file each existing type was read from, by id: a renamed type's file moves (D28).</summary>
    private Dictionary<Guid, string> existingFiles = new();

    /// <summary>Types whose name this run changes (D28): their translations and rights move with them at the end.</summary>
    private readonly List<(string OldName, string NewName)> renamedTypes = new();

    /// <summary>
    /// The renames the application states; <see langword="null"/>: those on the context type's assembly. Set by tests.
    /// Library types arrive renamed already (<see cref="SparkLayerCatalog"/>); this names the application's own CLR types.
    /// </summary>
    internal IReadOnlyList<PersistentObjectNameAttribute>? Renames { get; init; }

    /// <summary>Each library query's own <c>columnOrder</c> (#284 D16): a list the application has not changed is left to its library.</summary>
    private IReadOnlyDictionary<string, string[]> libraryColumnOrders = new Dictionary<string, string[]>(StringComparer.Ordinal);

    /// <summary>The libraries whose model layers the run composes; <see langword="null"/>: the process's (<see cref="SparkLayerCatalog"/>). Set by tests.</summary>
    internal IReadOnlyList<SparkLibrary>? Libraries { get; init; }

    /// <summary>
    /// <see cref="SparkOptions.ModelAttribute"/> (<c>docs/default_index_PRD.md</c>, D9): what a property becomes when its
    /// attribute is first created. The synchronize command passes the application's; the default is Spark's.
    /// </summary>
    internal Func<Type, PropertyInfo, ModelAttributeSeed> ModelAttribute { get; init; } = ModelAttributeConventions.AuditMembersHidden;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public void SynchronizeModels(Type contextType)
    {
        ArgumentNullException.ThrowIfNull(contextType);
        var modelPath = SparkAppData.Path(hostEnvironment.ContentRootPath, "Model");

        // Ensure directory exists
        Directory.CreateDirectory(modelPath);

        // Load existing entity types and their inline queries: the composed model, library types included
        LoadLibraryTypes();
        applicationNames = (Renames ?? SparkLayerCatalog.RenamesOf(contextType.Assembly))
            .Where(r => r.EntityType is { } type && !libraryTypesByClrType.ContainsKey(type.FullName ?? type.Name))
            .GroupBy(r => r.EntityType!.FullName ?? r.EntityType!.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);
        libraryColumnOrders = ColumnOrderMaintenance.LibraryColumnOrders(Libraries ?? SparkLayerCatalog.Libraries);
        MoveStaleDeltas(modelPath);
        var (existingEntityTypes, existingQueries) = LoadExistingEntityTypeFiles();

        // Find all IRavenQueryable<T> properties on the SparkContext
        var queryableProperties = contextType.GetCachedProperties()
            .Where(p => IsRavenQueryable(p.PropertyType))
            .ToList();

        // F7, before anything is written: an application class whose name a library type already has would have
        // become a delta on it (the library's id and rights over the application's documents, without the library's
        // row policies, which follow the CLR type). Refused, pointing at the remedies.
        RefuseNameCollisions(queryableProperties
            .Select(p => GetQueryableEntityType(p.PropertyType))
            .OfType<Type>()
            .Where(t => !t.IsSparkProjection())
            .Distinct());

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
            .Select(property => (Property: property, EntityType: GetQueryableEntityType(property.PropertyType)))
            .Where(x => x.EntityType is not null)
            .Where(x =>
            {
                // Projection types are merged into their collection type's JSON file
                if (!x.EntityType!.IsSparkProjection()) return true;
                Console.WriteLine($"Skipping projection type: {x.EntityType!.Name} (merged into collection type)");
                return false;
            })
            .GroupBy(x => x.EntityType!)
            .ToList();

        // Each entity type's queries, retargeted first (#276 pre-pass, see RetargetRenamedDatabaseQueries): a
        // renamed context property otherwise leaves the old query behind with a dead "Database.OldName" source
        // (silently returning no rows) AND mints a duplicate for the new name. Exactly one dead Database.* source
        // plus exactly one unclaimed property is a rename with high confidence — retarget the existing query in
        // place, preserving its Id (program units reference queries by id) and all authoring. Anything else is
        // ambiguous: warn and keep, never guess, never delete. Custom.* queries are never touched.
        // Done for every type before any query is planned, so the names and aliases the plan steps around are the
        // ones that will be written.
        var queriesByType = new Dictionary<Type, List<SparkQuery>>();
        foreach (var group in rootsByEntityType)
        {
            var queriesForType = CollectQueriesFor(existingQueries, group.Key, existingEntityTypes);
            RetargetRenamedDatabaseQueries(queriesForType, group.Select(x => x.Property.Name).ToList(), NameOf(group.Key));
            queriesByType[group.Key] = queriesForType;
        }

        // Every root's query, existing or to be minted, named before any model file is built: a reference on a type
        // processed early must name the query minted for a root processed late (D6, D11).
        // Satellites (below) mint queries too, so they are planned in the same pass: a library-named satellite query
        // (a contributions query, say) and a minted root query must not take each other's name or alias either.
        var satellites = CollectSatellites(rootsByEntityType);
        var satelliteQueriesByType = satellites.ToDictionary(s => s.ModelType, s => CollectQueriesFor(existingQueries, s.ModelType, existingEntityTypes));
        var (rootQueries, satelliteQueryPlans) = PlanRootQueries(rootsByEntityType, queriesByType, satellites, satelliteQueriesByType, existingQueries);
        var referenceQueries = ResolveReferenceQueries(contextType, rootsByEntityType, rootQueries);

        foreach (var group in rootsByEntityType)
        {
            var entityType = group.Key;
            var clrType = entityType.FullName ?? entityType.Name;

            // The entity's default index: the one claiming it with [DefaultIndex], else its sole
            // index with a view (docs/default_index_PRD.md, D3). Null when it has neither — then the
            // file carries no binding (that invariant is hash-relevant, see ModelShapeDiscovery).
            var defaultEntry = indexCatalog.GetDefaultFor(entityType);
            Type? projectionType = defaultEntry?.ProjectionType;
            string? indexName = defaultEntry?.IndexName;

            // A viewless default (SP4) is queried for entity documents, but RavenDB can only sort and
            // filter on what its maps emit: anything else is "The field '…' is not indexed", a 500.
            // Null for a viewed default or no default — the view is the surface then.
            var querySurface = defaultEntry is { ProjectionType: null } ? ViewlessQuerySurface(defaultEntry) : null;

            // Find or create entity type definition (merging with projection type if present)
            var existingDef = existingEntityTypes.Values.FirstOrDefault(e => e.ClrType == clrType);
            var entityTypeDef = CreateOrUpdateEntityTypeDefinition(entityType, projectionType, indexName, existingDef, referenceQueries, querySurface);
            ApplyRendererSeeds(entityTypeDef, entityType);

            // Existing inline queries for this entity type (already retargeted above), plus a default if missing
            var queriesForType = queriesByType[entityType];

            // Query indexName stamping + maintenance (#279). The field is load-bearing at runtime,
            // so every Database.* query gets an explicit binding. Provenance is stored==derived:
            //   empty              -> stamp the entity default (behavior-preserving: empty already
            //                         fell back to the entity file's binding, which is the default)
            //   == derived default -> machine-owned, already correct
            //   != derived, known  -> authored (a deliberate non-default index), preserved verbatim
            //   != derived, dead   -> the named index no longer exists (renamed or removed):
            //                         retarget to the default (or clear) with a console note —
            //                         failing here would leave renames unrepairable by synchronize
            foreach (var databaseQuery in queriesForType.Where(q =>
                         q.Source.StartsWith("Database.", StringComparison.Ordinal)))
            {
                var stored = databaseQuery.IndexName;
                if (string.Equals(stored, indexName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (string.IsNullOrEmpty(stored))
                {
                    if (indexName is null) continue;
                    databaseQuery.IndexName = indexName;
                    Console.WriteLine($"Stamped indexName '{indexName}' on query '{databaseQuery.Name}'.");
                }
                else if (indexCatalog.GetByIndexName(stored) is null)
                {
                    databaseQuery.IndexName = indexName;
                    Console.WriteLine(indexName is null
                        ? $"Cleared indexName '{stored}' on query '{databaseQuery.Name}': no index with that name exists."
                        : $"Retargeted indexName '{stored}' -> '{indexName}' on query '{databaseQuery.Name}': no index named '{stored}' exists.");
                }
                // else: authored binding to a known non-default index — preserved verbatim.
            }

            // After the bindings above settle: D10 reads a query's index to decide how an append is written.
            MaintainColumnOrders(entityTypeDef, queriesForType);

            // One default query per context property exposing this type, under the name and alias planned for it.
            foreach (var property in group.Select(x => x.Property))
            {
                var plan = rootQueries[property.Name];
                if (plan.Existing is not null)
                    continue;

                var queryName = plan.Name;
                queriesForType.Add(new SparkQuery
                {
                    Id = Guid.NewGuid(),
                    Name = queryName,
                    // Null unless the derived alias was taken (D11): then the first free one, recorded so the
                    // model file states the URL the query is reachable at.
                    Alias = plan.Alias,
                    // Set eagerly, though LoadExistingEntityTypeFiles would derive the same value on
                    // the next read. Leaving it null made synchronization non-idempotent: the first
                    // run omitted the field, the second read it back, populated it and wrote it out,
                    // so two consecutive runs produced different bytes.
                    EntityType = entityTypeDef.Name,
                    Source = $"Database.{property.Name}",
                    // Stamped at mint (#279): the query declares its index instead of relying on
                    // an ambient default. Null when the entity has no projection-bearing index.
                    IndexName = indexName,
                    SortColumns = GetDefaultSortProperty(entityTypeDef, querySurface) is string sortProp
                        ? [new SortColumn { Property = sortProp, Direction = "asc" }]
                        : []
                });
                Console.WriteLine($"Created query: {queryName} (inline in {entityTypeDef.Name}.json)");
                if (plan.Name != $"Get{property.Name}")
                    Console.WriteLine($"  Named it '{plan.Name}': 'Get{property.Name}' is another query's name.");
                if (plan.Alias is not null)
                    Console.WriteLine($"  Gave it the alias '{plan.Alias}': '{SparkQueryAliases.Derive(plan.Name)}' is another query's alias.");
            }

            // Save the entity type file with inline queries
            var fileName = Path.Combine(modelPath, $"{entityTypeDef.Name}.json");
            var entityTypeFile = new EntityTypeFile
            {
                PersistentObject = entityTypeDef,
                // Name-sorted for the same reason as the attributes: stable across runs and
                // merge-friendly. Nothing depends on the order of this array.
                Queries = [.. queriesForType
                    .OrderBy(q => q.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(q => q.Name, StringComparer.Ordinal)]
            };
            WriteModelFile(fileName, entityTypeFile);
            MoveRenamedFile(fileName, entityTypeDef);
            writtenFiles.Add(fileName);
            processedTypes.Add(clrType);

            // Also mark projection type as processed (no separate JSON file)
            if (projectionType != null)
            {
                var projectionClrType = projectionType.FullName ?? projectionType.Name;
                processedTypes.Add(projectionClrType);
                Console.WriteLine($"Synchronized model: {entityTypeDef.Name} (merged with {projectionType.Name}) -> {fileName}");
            }
            else
            {
                Console.WriteLine($"Synchronized model: {entityTypeDef.Name} -> {fileName}");
            }

            // Collect embedded types from this entity
            CollectEmbeddedTypes(entityType, entityTypeDef, typesToProcess, processedTypes);

            // Also collect embedded types from projection type (if any)
            if (projectionType != null)
            {
                CollectEmbeddedTypes(projectionType, entityTypeDef, typesToProcess, processedTypes);
            }
        }

        // Satellite types (contributions M5b): documents that belong to a context entity without being
        // a context property — a generated contribution type, say. Each gets a model file like a root,
        // and the query registered for it is minted once (the file owns it afterwards, as for roots).
        foreach (var satellite in satellites)
        {
            var satelliteType = satellite.ModelType;
            var clrType = satelliteType.FullName ?? satelliteType.Name;
            if (processedTypes.Contains(clrType))
                continue;

            var existingDef = existingEntityTypes.Values.FirstOrDefault(e => e.ClrType == clrType);
            var entityTypeDef = CreateOrUpdateEntityTypeDefinition(satelliteType, projectionType: null, indexName: null, existingDef, referenceQueries);
            ApplyRendererSeeds(entityTypeDef, satelliteType);

            var satelliteQueries = satelliteQueriesByType[satelliteType];
            MaintainColumnOrders(entityTypeDef, satelliteQueries);
            if (satellite.Query is { } minted
                && satelliteQueryPlans.TryGetValue(satelliteType, out var plan) && plan.Existing is null)
            {
                satelliteQueries.Add(new SparkQuery
                {
                    Id = Guid.NewGuid(),
                    Name = plan.Name,
                    // As for a root (D11): null unless the derived alias was taken.
                    Alias = plan.Alias,
                    EntityType = entityTypeDef.Name,
                    Source = minted.Source,
                    SortColumns = minted.SortProperty is { } sort
                        ? [new SortColumn { Property = sort, Direction = minted.SortDirection }]
                        : [],
                });
                Console.WriteLine($"Created query: {plan.Name} (inline in {entityTypeDef.Name}.json)");
                // A library names its satellite query because something addresses it by that name or its derived
                // alias (the contributions page reads 'contributionsQuery', ContributionDescriptor). Stepping aside
                // keeps the model loadable, but that contract no longer holds: say so loudly.
                if (plan.Name != minted.Name)
                    Console.WriteLine($"Warning: named it '{plan.Name}': '{minted.Name}' is another query's name. " +
                                      $"Whatever {satellite.OwnerType.Name}'s library addresses by '{minted.Name}' will not find it; rename the other query.");
                if (plan.Alias is not null)
                    Console.WriteLine($"Warning: gave it the alias '{plan.Alias}': '{SparkQueryAliases.Derive(plan.Name)}' is another query's alias. " +
                                      "Whatever addresses it by the derived alias will not find it; rename the other query.");
            }

            var fileName = Path.Combine(modelPath, $"{entityTypeDef.Name}.json");
            var entityTypeFile = new EntityTypeFile
            {
                PersistentObject = entityTypeDef,
                Queries = [.. satelliteQueries
                    .OrderBy(q => q.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(q => q.Name, StringComparer.Ordinal)]
            };
            WriteModelFile(fileName, entityTypeFile);
            MoveRenamedFile(fileName, entityTypeDef);
            writtenFiles.Add(fileName);
            processedTypes.Add(clrType);
            Console.WriteLine($"Synchronized model (satellite of {satellite.OwnerType.Name}): {entityTypeDef.Name} -> {fileName}");

            CollectEmbeddedTypes(satelliteType, entityTypeDef, typesToProcess, processedTypes);
        }

        // Process embedded types
        while (typesToProcess.Count > 0)
        {
            var embeddedType = typesToProcess.Dequeue();
            var clrType = embeddedType.FullName ?? embeddedType.Name;

            if (processedTypes.Contains(clrType))
                continue;

            var existingDef = existingEntityTypes.Values.FirstOrDefault(e => e.ClrType == clrType);
            var entityTypeDef = CreateOrUpdateEntityTypeDefinition(embeddedType, projectionType: null, indexName: null, existingDef, referenceQueries);
            ApplyRendererSeeds(entityTypeDef, embeddedType);

            // Preserve any existing inline queries for this embedded type
            var embeddedQueries = CollectQueriesFor(existingQueries, embeddedType, existingEntityTypes).ToArray();
            MaintainColumnOrders(entityTypeDef, embeddedQueries);

            var fileName = Path.Combine(modelPath, $"{entityTypeDef.Name}.json");
            var entityTypeFile = new EntityTypeFile
            {
                PersistentObject = entityTypeDef,
                Queries = [.. embeddedQueries
                    .OrderBy(q => q.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(q => q.Name, StringComparer.Ordinal)]
            };
            WriteModelFile(fileName, entityTypeFile);
            MoveRenamedFile(fileName, entityTypeDef);
            writtenFiles.Add(fileName);
            processedTypes.Add(clrType);

            Console.WriteLine($"Synchronized model (embedded): {entityTypeDef.Name} -> {fileName}");

            // Recursively collect embedded types from this type
            CollectEmbeddedTypes(embeddedType, entityTypeDef, typesToProcess, processedTypes);
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
        foreach (var entry in indexCatalog.GetAllEntries())
        {
            if (entry.ProjectionType == null)
                continue;

            var staleModelFile = Path.Combine(modelPath, $"{entry.ProjectionType.Name}.json");
            if (writtenFiles.Contains(staleModelFile) || !File.Exists(staleModelFile))
                continue;

            File.Delete(staleModelFile);
            Console.WriteLine($"Removed stale projection model file: {entry.ProjectionType.Name}.json");
        }

        // D28: a type renamed in this run takes its translations and the application's rights along. Rights move only
        // while the old name is free; when another type now has it, the rights are listed for re-targeting by hand.
        if (renamedTypes.Count > 0)
        {
            var names = SparkModelFiles.ComposeLenient(Libraries ?? SparkLayerCatalog.Libraries, modelPath).Types
                .Select(t => t.Name)
                .OfType<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!ModelRenameMigration.Apply(hostEnvironment.ContentRootPath, renamedTypes, names))
                Environment.ExitCode = 2;
        }

        TranslationsSeeder.Apply(hostEnvironment.ContentRootPath, seeds);
        ReportMissingTranslations();

        // Last of the file edits (#264, G-Q17), so a file the seeder rewrote is covered too. Before the
        // hashes only by habit: $schema is not part of any hashed shape.
        foreach (var path in SparkSchemaReference.ApplyAll(hostEnvironment.ContentRootPath, SparkSchemaRevision.Current))
            Console.WriteLine($"Pointed $schema at schema revision v{SparkSchemaRevision.Current}: {path}");

        if (WouldCertifyAnEmptyModel(contextType, queryableProperties.Count, modelPath))
            return;

        WriteModelHashes(contextType);
    }

    /// <summary>
    /// Refuses to stamp a hash file that certifies an empty model over a model directory that still
    /// has content in it.
    /// </summary>
    /// <remarks>
    /// A context with no <c>IRavenQueryable&lt;&gt;</c> properties produces no model files, and this
    /// method deletes none — so the directory keeps whatever a previous run wrote. Writing the hash
    /// file anyway records "the model is empty" alongside a populated directory, which is the one
    /// combination nothing downstream can detect: <c>--spark-verify-model</c> derives both sides of
    /// its comparison from the same context type and so agrees, while the running application derives
    /// the shape from its DI-resolved context and disagrees — surfacing as a startup failure in
    /// Production, a long way from the command that caused it.
    /// <para>
    /// An empty context with an empty directory is left alone: that is a new or deliberately empty
    /// application, not a mistake.
    /// </para>
    /// </remarks>
    private static bool WouldCertifyAnEmptyModel(Type contextType, int queryableRootCount, string modelPath)
    {
        if (queryableRootCount > 0)
            return false;

        if (!Directory.Exists(modelPath) || Directory.GetFiles(modelPath, "*.json").Length == 0)
            return false;

        Console.Error.WriteLine(
            $"Spark: '{contextType.Name}' declares no IRavenQueryable<> properties, but {modelPath} " +
            "already contains model files. Refusing to write a model hash that would certify an empty " +
            "model over them — the running application would then fail at startup instead. Check that " +
            "the context type passed to model synchronization is the application's own context.");
        Environment.ExitCode = 2;
        return true;
    }

    /// <summary>
    /// Records the fingerprint of the entity classes these model files were generated from, so a
    /// deployed application can tell that its model no longer describes its classes.
    /// </summary>
    private void WriteModelHashes(Type contextType)
    {
        // Computed after every model file has been written, so the file hash covers the output of
        // this same run.
        var hashFile = BuildModelHashes(contextType, indexCatalog, hostEnvironment.ContentRootPath);
        hashFile.Write(hostEnvironment.ContentRootPath);

        Console.WriteLine($"Model hash: {hashFile.ModelHash} -> {ModelHashFile.PathFor(hostEnvironment.ContentRootPath)}");
    }

    /// <summary>
    /// Computes the hash file for a context type. Shared with the startup check so the value written
    /// and the value verified can never be produced by two different pieces of code.
    /// </summary>
    /// <summary>
    /// The <c>translations.json</c> key an attribute's description lives under (#467, D4): the
    /// explicit key the model file names, else <c>model.{Entity}.attributes.{Attr}.description</c>.
    /// </summary>
    internal static string DescriptionKey(string entityName, EntityAttributeDefinition attribute)
        => attribute.Description?.Key ?? $"model.{entityName}.attributes.{attribute.Name}.description";

    /// <summary>
    /// Who owns an attribute's description (#348, revised; moved to translations.json by #467, D5):
    /// the C# summary is a SEED, never an overwrite. It fills <c>en</c> under the description key
    /// when no layer defines it — absent, or present but blank — and translations.json owns the value
    /// from then on, in every language.
    /// </summary>
    /// <remarks>
    /// A <c>///</c> comment is written for the next developer, while a description renders as an
    /// [i] tooltip for the end user; the comment is the wrong source once somebody has written real
    /// help text. So a corrected summary does not reach a description that already has text.
    /// </remarks>
    private void CollectDescriptionSeed(string entityName, EntityAttributeDefinition attribute, string? seed)
    {
        // A library type's descriptions belong in the library's translations.json, not the app's (D6).
        if (seed is not null && !libraryTypes.ContainsKey(entityName))
            seeds.Add((DescriptionKey(entityName, attribute), seed));
    }

    /// <summary>
    /// For <c>--spark-verify-model</c>: every attribute that C# can describe but the model file
    /// leaves empty (#348, revised). The structural hash ignores descriptions by design, so this is
    /// the only place a missing English description is caught.
    /// <para>
    /// Deliberately NOT a difference check. Since <see cref="ApplyDescriptionSeed"/> seeds and never
    /// replaces, a description that diverges from the C# summary is a human's wording — reporting it
    /// would fail the build over text that synchronize would not change.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string> DescribeDescriptionDrift(Type contextType, string contentRootPath)
    {
        var entityTypes = contextType.GetCachedProperties()
            .Where(p => IsRavenQueryable(p.PropertyType))
            .Select(p => GetQueryableEntityType(p.PropertyType))
            .Where(t => t is not null)
            .Cast<Type>()
            .ToList();

        // Embedded (AsDetail) types have model files too; they live in the same assemblies.
        var assemblies = entityTypes.Select(t => t.Assembly).Append(contextType.Assembly).Distinct().ToList();
        var catalog = new AttributeDescriptionCatalog(_ => { });
        var seeds = new List<(string Key, string Text)>();

        foreach (var file in SparkModelFiles.Compose(contentRootPath))
        {
            // A library type's descriptions are the library's to ship; synchronize seeds none (D6).
            if (file.Library is not null)
                continue;

            EntityTypeFile? model;
            try
            {
                model = JsonSerializer.Deserialize<EntityTypeFile>(
                    file.Json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException)
            {
                continue;
            }

            var definition = model?.PersistentObject;
            if (definition?.ClrType is null)
                continue;

            var clrType = assemblies.Select(a => a.GetType(definition.ClrType)).FirstOrDefault(t => t is not null);
            if (clrType is null)
                continue;

            var properties = clrType.GetSparkModelProperties().ToDictionary(p => p.Name, p => p);
            foreach (var attribute in definition.Attributes)
            {
                if (!properties.TryGetValue(attribute.Name, out var property))
                    continue;

                var seed = catalog.Seed(property);
                if (seed is not null)
                    seeds.Add((DescriptionKey(definition.Name, attribute), seed));
            }
        }

        // Only what synchronize would actually write is drift (#467, D5: verify fails exactly when
        // sync would write). A description that merely differs from the C# summary is a human's
        // wording and is left alone, so it must not fail verification either.
        return TranslationsSeeder.Pending(contentRootPath, seeds)
            .Select(s => $"{s.Key}: no layer of translations.json defines 'en', C# says \"{s.Text}\"")
            .ToList();
    }

    internal static ModelHashFile BuildModelHashes(Type contextType, IIndexCatalog indexCatalog, string contentRootPath)
    {
        var shapes = ModelShapeDiscovery.Discover(contextType, indexCatalog);
        var perEntity = SparkModelShape.ComputePerEntityHashes(shapes);
        var contextRoots = SparkModelShape.ComputeContextRootsHash(
            ModelShapeDiscovery.RootEntityNames(contextType));
        var fileHashes = ModelHashFile.ComputeFileHashes(contentRootPath);
        var modelFiles = ModelHashFile.CombineFileHashes(fileHashes);
        var configHashes = ModelHashFile.ComputeConfigHashes(contentRootPath);
        var (libraries, layers) = ModelHashFile.ComputeLayers(contentRootPath);

        return new ModelHashFile
        {
            ModelHash = SparkModelShape.ComputeModelHash(
                perEntity, contextRoots, modelFiles,
                configHashes.Count > 0 ? ModelHashFile.CombineFileHashes(configHashes) : null,
                ModelHashFile.CombineLayerHashes(layers)),
            ContextRoots = contextRoots,
            ModelFiles = modelFiles,
            Files = fileHashes,
            ConfigFiles = configHashes.Count > 0 ? configHashes : null,
            Libraries = libraries.Count > 0 ? libraries : null,
            Layers = layers.Count > 0 ? layers : null,
            Names = ModelHashFile.ComputeNames(contentRootPath) is { Count: > 0 } names ? names : null,
            Entities = new SortedDictionary<string, string>(perEntity.ToDictionary(e => e.Key, e => e.Value), StringComparer.Ordinal),
        };
    }

    /// <summary>
    /// Writes the renderer a library seeded (<see cref="SparkModelSatellites.SeedRenderer"/>) onto an
    /// attribute that has none. Never overwrites: once written, the model file owns the renderer and
    /// its options, so a second run reads them back unchanged (a fixed point).
    /// </summary>
    private static void ApplyRendererSeeds(EntityTypeDefinition definition, Type type)
    {
        var seeds = SparkModelSatellites.RendererSeedsFor(type);
        foreach (var attribute in definition.Attributes)
        {
            if (!string.IsNullOrEmpty(attribute.Renderer))
                continue;

            if (seeds.TryGetValue(attribute.Name, out var seed))
            {
                attribute.Renderer = seed.Renderer;
                attribute.RendererOptions = seed.Options is null ? null : new Dictionary<string, object>(seed.Options);
            }
            // A Money attribute (commerce X4) is an object on the wire, so without a renderer ng-spark has
            // nothing to draw. The core `money` renderer is the default seed for every one of them, after
            // a library's own seed, and on the same terms: only where none is set, and the model file owns
            // it from then on.
            else if (attribute.DataType == SparkMoney.DataType)
            {
                attribute.Renderer = SparkMoney.RendererName;
            }
            // Quantity (warehouse WX8), on the same terms.
            else if (attribute.DataType == SparkQuantity.DataType)
            {
                attribute.Renderer = SparkQuantity.RendererName;
            }
        }
    }

    private void CollectEmbeddedTypes(Type entityType, EntityTypeDefinition definition, Queue<Type> typesToProcess, HashSet<string> processedTypes)
    {
        // Shares the model-property filter with CreateOrUpdateEntityTypeDefinition on purpose:
        // discovery and attribute generation must agree, or an ignored complex property would
        // still get its type written out as an embedded model file that nothing references.
        var properties = entityType.GetSparkModelProperties();

        foreach (var property in properties)
        {
            // Only a property that has an attribute in the definition just written pulls its type in. The
            // ModelAttribute hook's Exclude (D9) creates no attribute, so its type would be a model file nothing
            // references. An attribute that already existed keeps its type, whatever the hook says now (#253).
            if (!definition.Attributes.Any(a => a.Name == property.Name))
                continue;

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
    /// <remarks>
    /// The queries are found under the name the type has on disk (its definition is found by CLR type), so a type
    /// renamed in this run (D28) keeps them; their <c>entityType</c> is moved to the new name.
    /// </remarks>
    private List<SparkQuery> CollectQueriesFor(List<SparkQuery> existingQueries, Type type, Dictionary<Guid, EntityTypeDefinition> existingEntityTypes)
    {
        var clrType = type.FullName ?? type.Name;
        var name = NameOf(type);
        var onDisk = existingEntityTypes.Values.FirstOrDefault(e => e.ClrType == clrType)?.Name ?? name;
        var queries = CollectQueriesFor(existingQueries, onDisk);
        if (!string.Equals(onDisk, name, StringComparison.Ordinal))
            foreach (var query in queries)
                query.EntityType = name;
        return queries;
    }

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

    /// <summary>
    /// Keeps each existing <c>columnOrder</c> over <paramref name="rowType"/> complete and alive (#284 R5,
    /// <see cref="ColumnOrderMaintenance"/>), with a console note per change. A null list is never created
    /// (D4), and a list its library owns is left alone (D16).
    /// </summary>
    private void MaintainColumnOrders(EntityTypeDefinition rowType, IEnumerable<SparkQuery> queries)
    {
        foreach (var query in queries)
        {
            if (ColumnOrderMaintenance.IsLibraryOwned(query, libraryColumnOrders)
                || ColumnOrderMaintenance.Maintain(rowType, query, indexCatalog) is not { } maintained)
                continue;

            query.ColumnOrder = maintained.ColumnOrder;
            foreach (var change in maintained.Changes)
                Console.WriteLine(change.Note(query.Name));
        }
    }

    /// <summary>
    /// The composed model (composition D6) as the run starts from: a library type arrives with the
    /// application's delta already on top, so what is synchronized is what the server would load.
    /// </summary>
    private (Dictionary<Guid, EntityTypeDefinition> EntityTypes, List<SparkQuery> Queries) LoadExistingEntityTypeFiles()
    {
        var entityTypes = new Dictionary<Guid, EntityTypeDefinition>();
        var queries = new List<SparkQuery>();

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        foreach (var file in SparkModelFiles.Compose(Libraries ?? SparkLayerCatalog.Libraries, SparkAppData.Path(hostEnvironment.ContentRootPath, "Model")))
        {
            try
            {
                var json = file.Json;
                // Rewriting the file would strip "isVisible": false and show a hidden attribute, so a
                // legacy hidden attribute stops the command (#264, G-Q8); the catch below lets it
                // through. "isVisible": true is dropped by the rewrite, the property no longer existing.
                ModelLoader.RefuseLegacyIsVisible(json, file.Source);
                var entityTypeFile = JsonSerializer.Deserialize<EntityTypeFile>(json, jsonOptions);
                if (entityTypeFile?.PersistentObject != null)
                {
                    var entityType = entityTypeFile.PersistentObject;
                    entityTypes[entityType.Id] = entityType;
                    if (file.AppFile is { } appFile) existingFiles[entityType.Id] = appFile;

                    // Extract inline queries, auto-populating EntityType from the containing file
                    foreach (var query in entityTypeFile.Queries)
                    {
                        query.EntityType ??= entityType.Name;
                        queries.Add(query);
                    }
                }
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                // Fail closed (docs/coverage_merge_queue_PRD.md S7). Skipping the file used to regenerate the
                // entity as new: fresh GUIDs over the ids other files reference, and the hand edits dropped,
                // with exit 0. A rebase that leaves conflict markers in the file is the usual cause.
                throw new ModelFileUnreadableException(
                    $"Model file {file.Source} cannot be read: {ex.Message}" + Environment.NewLine
                    + "Synchronization stopped before writing anything. Fix the file (a merge or rebase may have left "
                    + "conflict markers in it) and run the command again.", ex);
            }
        }

        return (entityTypes, queries);
    }

    /// <summary>
    /// The library model types by name, each as this synchronizer would serialize it, so that a
    /// delta compares like with like: a default the library leaves out is not a difference (D6).
    /// </summary>
    private void LoadLibraryTypes()
    {
        libraryTypes = new Dictionary<string, LibraryModelType>(StringComparer.OrdinalIgnoreCase);
        libraryTypesByClrType = new Dictionary<string, LibraryModelType>(StringComparer.Ordinal);
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        foreach (var type in SparkModelFiles.LibraryTypes(Libraries ?? SparkLayerCatalog.Libraries))
        {
            if (type.Name is null || type.Library is null || type.Identity is null) continue;
            var file = JsonSerializer.Deserialize<EntityTypeFile>(type.Json, jsonOptions);
            if (file?.PersistentObject is null) continue;
            var library = new LibraryModelType(
                type.Library,
                type.Identity,
                type.Name,
                file.PersistentObject.ClrType,
                (SparkJsonObject)SparkJson.Parse(JsonSerializer.Serialize(file, JsonOptions)));
            libraryTypes[type.Name] = library;
            if (library.ClrType is { } clrType) libraryTypesByClrType[clrType] = library;
        }
    }

    /// <summary>
    /// The name a CLR type has in the model (D28): a library's class keeps the library type's (effective) name,
    /// an application class the name <c>[assembly: PersistentObjectName&lt;T&gt;]</c> gives it, else its class name.
    /// </summary>
    private string NameOf(Type type)
    {
        var clrType = type.FullName ?? type.Name;
        if (libraryTypesByClrType.TryGetValue(clrType, out var library)) return library.Name;
        return applicationNames.TryGetValue(clrType, out var name) ? name : type.Name;
    }

    /// <summary>
    /// F7: an application class whose model name a library type already has (ignoring case, as rights and lookups
    /// do) used to become a delta on that type; the hijack is refused before anything is written.
    /// </summary>
    private void RefuseNameCollisions(IEnumerable<Type> types)
    {
        var problems = new List<string>();
        foreach (var type in types)
        {
            var clrType = type.FullName ?? type.Name;
            if (libraryTypesByClrType.ContainsKey(clrType)) continue;
            var name = NameOf(type);
            if (libraryTypes.TryGetValue(name, out var library))
            {
                problems.Add(
                    $"'{clrType}' would be named '{name}', the name of {library.Library}'s type '{library.Identity}'" +
                    (library.ClrType is null ? "" : $" ({library.ClrType})") + ". " +
                    $"Name yours with [assembly: PersistentObjectName<{clrType}>(\"<NewName>\")], or rename the library's with " +
                    $"[assembly: PersistentObjectName(\"{library.Identity.Split(':')[0]}\", \"{library.Identity.Split(':')[1]}\", \"<NewName>\")] (SPARK058).");
            }
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "Model synchronization stopped before writing anything: an application type would take a library type's name (F7)."
                + Environment.NewLine + string.Join(Environment.NewLine, problems.Select(p => "  - " + p)));
        }
    }

    /// <summary>
    /// Writes a type's model file (composition D6). A type the application owns is written whole. A
    /// library type is written as the delta against the library's layer, anchored on it with
    /// <c>"$extends": "alias:DeclaredName"</c> (D28): only what differs, never an id the library states;
    /// nothing at all when nothing differs, in which case no file is created.
    /// </summary>
    /// <remarks>
    /// The library type is found by CLR type, never by name (F7): only the library's own class is its type.
    /// </remarks>
    /// <returns>Whether a file was written.</returns>
    private bool WriteModelFile(string fileName, EntityTypeFile entityTypeFile)
    {
        var json = JsonSerializer.Serialize(entityTypeFile, JsonOptions);
        if (entityTypeFile.PersistentObject.ClrType is { } clrType && libraryTypesByClrType.TryGetValue(clrType, out var library))
        {
            var delta = SparkModelLayers.Delta(library.Layer, (SparkJsonObject)SparkJson.Parse(json), library.Identity);
            if (delta is null)
            {
                Console.WriteLine($"Library model: {entityTypeFile.PersistentObject.Name} is {library.Library}'s and nothing differs; no file written.");
                return false;
            }
            json = SparkJson.Write(delta, indented: true);
            Console.WriteLine($"Library model: {entityTypeFile.PersistentObject.Name} is {library.Library}'s; only the delta is written.");
        }

        File.WriteAllText(fileName, SparkSchemaReference.CarryOver(fileName, json));
        return true;
    }

    /// <summary>
    /// After a type's file is written under its (new) name, removes the file it was read from when that is another
    /// file (D28: a rename moves the file and keeps the id), and records the rename so its translations and
    /// rights follow at the end of the run.
    /// </summary>
    private void MoveRenamedFile(string fileName, EntityTypeDefinition definition)
    {
        if (!existingFiles.TryGetValue(definition.Id, out var previous)) return;
        if (string.Equals(Path.GetFullPath(previous), Path.GetFullPath(fileName), StringComparison.OrdinalIgnoreCase)) return;

        if (File.Exists(previous))
        {
            File.Delete(previous);
            Console.WriteLine($"Moved {Path.GetFileName(previous)} -> {Path.GetFileName(fileName)} (the type is now named '{definition.Name}'; its id is kept).");
        }
        existingFiles[definition.Id] = fileName;
    }

    /// <summary>
    /// D28: a delta whose <c>$extends</c> anchors on a library type that the application has since renamed is
    /// stale; the composer refuses it, so it is repaired here, before the model is read: its <c>name</c>, its
    /// queries' <c>entityType</c> and its file name follow the type's current name.
    /// </summary>
    private void MoveStaleDeltas(string modelPath)
    {
        if (!Directory.Exists(modelPath)) return;
        var byIdentity = libraryTypes.Values.ToDictionary(t => t.Identity, StringComparer.Ordinal);

        foreach (var path in Directory.GetFiles(modelPath, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            if (string.Equals(Path.GetFileName(path), ModelHashFile.FileName, StringComparison.OrdinalIgnoreCase)) continue;

            SparkJsonObject root;
            try
            {
                if (SparkJson.Parse(File.ReadAllText(path)) is not SparkJsonObject parsed) continue;
                root = parsed;
            }
            catch (SparkJsonException)
            {
                continue; // The composer reports it in its own words.
            }

            if (SparkModelLayers.ExtendsOf(root) is null
                && SparkModelLayers.NameOf(root) is { } bareName
                && libraryTypes.TryGetValue(bareName, out var named)
                && root["persistentObject"] is SparkJsonObject po && po["id"] is null
                && (SparkModelLayers.ClrTypeOf(root) is not { } stated || stated == named.ClrType))
            {
                // A delta written before $extends (D28): id-less and naming the library's own class (or none), so it
                // was the library's type all along; anchoring it changes nothing it means. A file mapping another
                // class is the F7 hijack and is left for the composer to refuse.
                var anchored = new SparkJsonObject(root.Comparer);
                foreach (var member in root.Members)
                {
                    anchored.Set(member.Key, member.Value);
                    if (member.Key == "$schema") anchored.Set(SparkLayers.ExtendsDirective, new SparkJsonString(named.Identity));
                }
                if (anchored[SparkLayers.ExtendsDirective] is null)
                {
                    var first = new SparkJsonObject(root.Comparer);
                    first.Set(SparkLayers.ExtendsDirective, new SparkJsonString(named.Identity));
                    foreach (var member in anchored.Members) first.Set(member.Key, member.Value);
                    anchored = first;
                }
                File.WriteAllText(path, SparkJson.Write(anchored, indented: true));
                Console.WriteLine($"Anchored the delta {Path.GetFileName(path)} on '{named.Identity}' (\"$extends\", D28).");
                root = anchored;
            }

            if (SparkModelLayers.ExtendsOf(root) is not { } identity || !byIdentity.TryGetValue(identity, out var library)) continue;
            if (SparkModelLayers.NameOf(root) is not { } name || name == library.Name) continue;

            var map = new Dictionary<string, string>(StringComparer.Ordinal) { [name] = library.Name };
            var json = SparkRenames.Rewrite(SparkLayerKinds.Model, SparkJson.Write(root), map);
            var target = Path.Combine(modelPath, library.Name + ".json");
            var samePath = string.Equals(Path.GetFullPath(target), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
            if (!samePath && File.Exists(target))
            {
                throw new InvalidOperationException(
                    $"{path} extends '{identity}', now named '{library.Name}', but {target} already exists. Merge the two files by hand, then run the command again.");
            }

            File.WriteAllText(target, SparkSchemaReference.CarryOver(path, json));
            if (!samePath) File.Delete(path);
            renamedTypes.Add((name, library.Name));
            Console.WriteLine($"Moved the delta {Path.GetFileName(path)} -> {Path.GetFileName(target)}: '{identity}' is now named '{library.Name}' (was '{name}').");
        }
    }

    /// <param name="querySurface">
    /// For a viewless default index, the fields its maps emit: a new attribute outside it is minted with
    /// <c>canSort</c>/<c>canFilter</c> false. Null when there is no such restriction.
    /// </param>
    private EntityTypeDefinition CreateOrUpdateEntityTypeDefinition(Type entityType, Type? projectionType, string? indexName, EntityTypeDefinition? existing, ReferenceQueries? referenceQueries = null, IReadOnlySet<string>? querySurface = null)
    {
        var name = NameOf(entityType);
        var entityTypeDef = existing ?? new EntityTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            ClrType = entityType.FullName ?? entityType.Name,
        };

        // Update basic info. A changed name (D28: [assembly: PersistentObjectName<T>] added, changed or removed) keeps
        // the id, which the synchronizer found by CLR type; the file moves and the type's translations and rights follow.
        if (existing is not null && !string.Equals(existing.Name, name, StringComparison.Ordinal)
            && !libraryTypesByClrType.ContainsKey(entityTypeDef.ClrType ?? string.Empty))
        {
            renamedTypes.Add((existing.Name, name));
            Console.WriteLine($"Renamed type '{existing.Name}' -> '{name}' (id {existing.Id} kept).");
        }
        entityTypeDef.Name = name;
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
        // The default index's name, view or not. It used to be cleared without a projection, which was
        // right while only a viewed index could be a default; a marked viewless default (D3/SP4) is bound
        // here like any other, and satellites/embedded types pass null.
        entityTypeDef.IndexName = indexName;

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
                $"{SparkAppData.Relative("Model")}/{entityTypeDef.Name}.json.");
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

        // The orders already in use. A new attribute takes its position in the property walk, as it always has, but
        // only when no existing attribute holds that order; otherwise the next free one after them all. Without
        // this, a root over a library type minted CreatedBy at an order the library's own attributes already held
        // (PRD §7, SP5), and the layout's tie-break decided where it appeared.
        var takenOrders = entityTypeDef.Attributes.Where(a => a.Order > 0).Select(a => a.Order).ToHashSet();

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
            // The collection property's text wins over the projection's; either may be undocumented.
            var descriptionSeed = (collectionProp is null ? null : descriptions.Seed(collectionProp))
                ?? (projectionProp is null ? null : descriptions.Seed(projectionProp));
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

            // [Sortable] takes effect on AsDetail arrays and on lists of plain values (List<string>,
            // int[] — the form's list editor reorders their items the same way). Not on a Reference
            // array, whose picker holds a set, nor on a scalar. Derived purely from the CLR shape
            // (like IsArray), so it's always refreshed. Null (not false) for the non-sortable case
            // keeps the flag absent from the model JSON.
            bool? isSortable = sortableAttr != null && isArray && dataType != "Reference" ? true : null;

            // A reference that names no query resolves to its target's plain root query (D6); an explicit
            // [Reference(typeof(T), "Q")] wins (D2).
            string? resolvedQuery = referenceAttr?.Query;
            if (resolvedQuery == null && referenceAttr != null && referenceQueries != null)
            {
                resolvedQuery = referenceQueries.Resolve(referenceAttr.TargetType);
            }
            var unresolvedReference = resolvedQuery is null && referenceAttr is not null && referenceQueries is not null;

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
                // A presentation-only override of a string property — MultiLineString, image, url —
                // says how to render something the CLR shape cannot describe, so a hand-set one is
                // kept rather than reset to "string" on every sync. Any other change still wins:
                // switching the property away from string clears it, because the hint would then be
                // describing a shape that no longer exists. See SparkStringPresentations.
                if (!SparkStringPresentations.Preserves(existingAttr.DataType, dataType))
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

                // ⚠️ IsSortable is derived (from [Sortable] on an AsDetail array) and so is assigned
                // above. CanSort/CanFilter/CanListDistincts (#431) are NOT, and must never be: they
                // are hand-authored capability flags with no CLR counterpart to derive from, and
                // assigning them here — including to null — would erase an author's intent on every
                // synchronize. They are absent from this branch on purpose, not by omission.
                //
                // The names are adjacent and the policies are opposite, which is the whole reason
                // this comment exists.

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
                    // An empty result self-heals to the derived capability — unless the author wrote
                    // `None` (#264, G-Q4): drawn nowhere is a choice, not an empty intersection, and an
                    // action shows it per object through the runtime ShowedOn. An absent showedOn reads
                    // as the default (both sides), so only that one is derived.
                    if (existingAttr.ShowedOn != EShowedOn.None)
                    {
                        var narrowedShowedOn = existingAttr.ShowedOn & showedOn;
                        existingAttr.ShowedOn = narrowedShowedOn != 0 ? narrowedShowedOn : showedOn;
                    }
                }
                else
                {
                    existingAttr.InCollectionType = null;
                    existingAttr.InQueryType = null;
                }

                CollectDescriptionSeed(entityTypeDef.Name, existingAttr, descriptionSeed);
                newAttributes.Add(existingAttr);
            }
            else
            {
                // A member the framework stamps (#271: [ReadOnly(true)], which the audit-members generator
                // emits) is created read-only, so the server refuses a posted value, and shown on the
                // object page only. Creation only, like IsReadOnly itself: an author's later edit stands.
                var stampedByFramework = property.GetCustomAttribute<System.ComponentModel.ReadOnlyAttribute>()?.IsReadOnly == true;
                if (stampedByFramework && (showedOn & EShowedOn.PersistentObject) != 0)
                    showedOn = EShowedOn.PersistentObject;

                // The application's hook (D9), here and only here: creation is the one moment the synchronizer
                // decides what a property becomes, so an existing attribute is never changed by it and nothing is
                // deleted (#253). Exclude creates nothing, deliberately without joining ignoredPropertyNames: that
                // set also drops a carried-over attribute, which the hook must never do. [IgnoreProperty] has
                // already vetoed its property above, whatever the hook would say.
                var seed = ModelAttribute(entityType, property);
                if (seed == ModelAttributeSeed.Exclude)
                    continue;

                // Hidden is Spark's "invisible" (showedOn None) plus read-only, so the server refuses a posted value.
                // Not required either: no form shows it, so nobody could supply one.
                var hidden = seed == ModelAttributeSeed.Hidden;
                if (hidden)
                    showedOn = EShowedOn.None;

                // Its place in the property walk, unless an existing attribute holds that order (see takenOrders).
                var newOrder = takenOrders.Contains(order) ? takenOrders.Max() + 1 : order;
                takenOrders.Add(newOrder);

                // Create new attribute
                var newAttr = new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(),
                    Name = propertyName,
                    // No label and no description in the model file (#467, D1/D4): the label is found
                    // under model.{Entity}.attributes.{Attr}.label, else the humanized name, and the
                    // description seed goes into translations.json.
                    DataType = dataType,
                    // A get-only property cannot be required: nothing can supply a value for it.
                    // Nor can a stamped one: the framework supplies it, never the person filling in the form.
                    IsRequired = property.CanWrite && !stampedByFramework && !hidden
                        && !IsNullable(property.PropertyType)
                        && property.PropertyType != typeof(string),
                    // Computed properties surface read-only rather than not at all. Only set on
                    // creation — the update branch never reassigns IsReadOnly, so a hand-set value
                    // survives re-synchronize.
                    IsReadOnly = !property.CanWrite || stampedByFramework || hidden,
                    Order = newOrder,
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

                // A viewless default's maps do not emit this field, so RavenDB cannot sort or filter on it
                // (SP4). Minted false, creation only: CanSort/CanFilter are the author's flags from then on
                // (see the update branch), so an author who adds the field to the map can lift them.
                if (querySurface is not null && !querySurface.Contains(propertyName))
                {
                    newAttr.CanSort = false;
                    newAttr.CanFilter = false;
                }
                CollectDescriptionSeed(entityTypeDef.Name, newAttr, descriptionSeed);
                newAttributes.Add(newAttr);
            }

            if (unresolvedReference)
                referenceQueries!.WarnUnresolved(entityType, property, referenceAttr!.TargetType,
                    readOnlyInModel: newAttributes.LastOrDefault(a => a.Name == propertyName) is { } written && (written.IsReadOnly || written.ShowedOn == EShowedOn.None));

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

            // Added as-is, by reference: **everything** rides along untouched, because nothing here
            // re-derives anything — this is the carry-over path for attributes with no CLR property
            // left to derive from. The Id matters most: clients key on it, so regenerating one
            // silently rewrites identity.
            //
            // This used to enumerate the fields that ride along (Id, Label, Rules, Renderer,
            // RendererOptions, Group, EditMode). The list went stale twice — it never gained
            // TriggersRefresh, ReferenceDisplayType or Description — so it is stated as a rule
            // instead. A new hand-authored field needs no edit here, which is the point.
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
    /// The satellites of the context's entity types, in processing order, each once (the first owner wins) and never a
    /// type that is itself a root: that one is written as a root.
    /// </summary>
    private static List<SparkSatelliteModelType> CollectSatellites(
        IReadOnlyList<IGrouping<Type, (PropertyInfo Property, Type? EntityType)>> rootsByEntityType)
    {
        var seen = rootsByEntityType.Select(g => g.Key).ToHashSet();
        return [.. rootsByEntityType
            .SelectMany(g => SparkModelSatellites.For(g.Key))
            .Where(s => seen.Add(s.ModelType))];
    }

    /// <summary>The query a context root is served by: an existing one, or the name and alias to mint it under.</summary>
    /// <param name="Alias">Set only when the minted query's derived alias was taken (D11).</param>
    private sealed record RootQueryPlan(string Name, string? Alias, SparkQuery? Existing);

    /// <summary>
    /// Names every root's query (<c>docs/default_index_PRD.md</c>, D11): the query the root already has, else
    /// <c>Get{Property}</c> — or, when another query holds that name or its alias, the first free one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A root's existing query is the one of its type named <c>Get{Property}</c>, as always, or one this rule named
    /// before (<c>Get{Property}2</c>, … over <c>Database.{Property}</c>). The second keeps a stepped-aside query a fixed
    /// point: without it the next run would look for <c>Get{Property}</c>, miss, and mint again.
    /// </para>
    /// <para>
    /// Names and aliases are taken across the <b>composed</b> model, library queries included. SP3 found the collision:
    /// <c>libs/organizations</c> ships <c>Custom.Organizations</c> under the alias <c>organizations</c>, so the
    /// <c>GetOrganizations</c> minted for an application's <c>Organizations</c> root derived the same alias, and
    /// <c>--spark-verify-model</c> exited 3 and startup threw (<c>SparkQueryAliasIndex</c>). A name matters as much: the
    /// client fetches a reference's options by the query's name, and the first query of that name answers.
    /// </para>
    /// <para>
    /// Existing queries are never renamed: program units and menus link to their ids and aliases. Only the query being
    /// minted steps aside, and the model file records the alias it got.
    /// </para>
    /// <para>
    /// Satellite queries (<see cref="SparkModelSatellites"/>) are minted in the same run, so they are planned here too,
    /// and <b>first</b>: their names are a library's contract (the contributions page addresses its query by the alias
    /// derived from the name), while a root's <c>Get{Property}</c> is only a convention. So a minted root steps around a
    /// satellite's name, never the reverse. A satellite steps aside only from a query that already exists, under the
    /// same rule, and the synchronizer warns then. Its fixed point is the same: the query of its type named as the
    /// library names it, or a stepped-aside one over the same source.
    /// </para>
    /// </remarks>
    private static (Dictionary<string, RootQueryPlan> Roots, Dictionary<Type, RootQueryPlan> Satellites) PlanRootQueries(
        IReadOnlyList<IGrouping<Type, (PropertyInfo Property, Type? EntityType)>> rootsByEntityType,
        IReadOnlyDictionary<Type, List<SparkQuery>> queriesByType,
        IReadOnlyList<SparkSatelliteModelType> satellites,
        IReadOnlyDictionary<Type, List<SparkQuery>> satelliteQueriesByType,
        IReadOnlyList<SparkQuery> existingQueries)
    {
        var takenNames = existingQueries.Select(q => q.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var takenAliases = existingQueries
            .Select(q => q.Alias ?? SparkQueryAliases.Derive(q.Name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var satellitePlans = new Dictionary<Type, RootQueryPlan>();
        foreach (var satellite in satellites)
        {
            if (satellite.Query is not { } minted)
                continue;
            satellitePlans[satellite.ModelType] = Plan(satelliteQueriesByType[satellite.ModelType], minted.Name, minted.Source);
        }

        var plans = new Dictionary<string, RootQueryPlan>(StringComparer.Ordinal);
        foreach (var group in rootsByEntityType)
        {
            var queriesForType = queriesByType[group.Key];
            foreach (var (property, _) in group)
                plans[property.Name] = Plan(queriesForType, $"Get{property.Name}", $"Database.{property.Name}");
        }

        return (plans, satellitePlans);

        RootQueryPlan Plan(List<SparkQuery> queriesForType, string conventional, string source)
        {
            var existing = queriesForType.FirstOrDefault(q => q.Name == conventional)
                ?? queriesForType.FirstOrDefault(q => q.Source == source && IsSteppedAside(q.Name, conventional));
            if (existing is not null)
                return new RootQueryPlan(existing.Name, existing.Alias, existing);

            var name = FirstFree(conventional, takenNames, i => $"{conventional}{i}");
            var derived = SparkQueryAliases.Derive(name);
            var alias = takenAliases.Contains(derived) ? FirstFree(derived, takenAliases, i => $"{derived}-{i}") : null;

            takenNames.Add(name);
            takenAliases.Add(alias ?? derived);
            return new RootQueryPlan(name, alias, null);
        }

        static string FirstFree(string wanted, HashSet<string> taken, Func<int, string> candidate)
        {
            if (!taken.Contains(wanted)) return wanted;
            for (var i = 2; ; i++)
                if (!taken.Contains(candidate(i))) return candidate(i);
        }

        static bool IsSteppedAside(string name, string conventional)
            => name.Length > conventional.Length
               && name.StartsWith(conventional, StringComparison.Ordinal)
               && name[conventional.Length..].All(char.IsAsciiDigit);
    }

    /// <summary>
    /// Which query a query-less <c>[Reference(typeof(T))]</c> resolves to (D6): the query of T's <b>plain</b> root, the
    /// one whose body is <c>Session.Query&lt;T&gt;()</c>.
    /// </summary>
    /// <remarks>
    /// The synchronizer reflects at runtime and cannot read a getter's body, so the generator classifies the roots and
    /// records the plain ones as <c>[SparkPlainRoot(typeof(T), nameof(Prop))]</c> on the context's generated partial.
    /// The attribute is <c>Inherited = false</c>: a base context carries the markers of its own roots (from its own
    /// generator run when it lives in another assembly), so the base types are walked here, the context's own markers
    /// first. A root that is not plain — filtered, bound to an index, customized, or a helper call — is never a
    /// reference's lookup by default, and a view root mints no query at all. This replaces a map from T to the
    /// <em>last</em> root of T, which picked <c>ActiveCars</c> over <c>Cars</c> by declaration order.
    /// </remarks>
    private static ReferenceQueries ResolveReferenceQueries(
        Type contextType,
        IReadOnlyList<IGrouping<Type, (PropertyInfo Property, Type? EntityType)>> rootsByEntityType,
        IReadOnlyDictionary<string, RootQueryPlan> rootQueries)
    {
        var rootsByType = rootsByEntityType.ToDictionary(
            g => g.Key,
            g => (IReadOnlyList<string>)[.. g.Select(x => x.Property.Name)]);

        var plainQueries = new Dictionary<Type, string>();
        for (var current = contextType; current is not null && current != typeof(object); current = current.BaseType)
        {
            foreach (var marker in current.GetCustomAttributes<SparkPlainRootAttribute>(inherit: false)
                         .OrderBy(m => m.PropertyName, StringComparer.Ordinal))
            {
                // A marker names a root of its own type; one the synchronizer did not see as a root (a view, a
                // property since removed by hand) mints nothing, so it resolves nothing either.
                if (!rootsByType.TryGetValue(marker.EntityType, out var roots) || !roots.Contains(marker.PropertyName))
                    continue;

                plainQueries.TryAdd(marker.EntityType, rootQueries[marker.PropertyName].Name);
            }
        }

        return new ReferenceQueries(plainQueries, rootsByType);
    }

    /// <summary>The reference lookups of one synchronize run, and the warning for a reference that has none (D8).</summary>
    private sealed class ReferenceQueries(
        IReadOnlyDictionary<Type, string> plainQueries,
        IReadOnlyDictionary<Type, IReadOnlyList<string>> rootsByType)
    {
        public string? Resolve(Type target) => plainQueries.GetValueOrDefault(target);

        /// <summary>
        /// The synchronizer's side of SPARK056, which also covers what the analyzer cannot see (a model the JSON alone
        /// declares, a context in another project). Without a query the edit page's picker has no options.
        /// </summary>
        /// <remarks>
        /// It warns only for a writable reference, as the analyzer does (D8): a read-only one (no setter, or
        /// <c>[ReadOnly(true)]</c>, which the generated audit members carry) opens no picker, so a missing query costs
        /// it nothing, and some types (<c>SparkUser</c>) have no root on purpose. That holds whether T has no root at
        /// all or roots none of which is plain; the message then names the roots it passed over. The model file counts
        /// too, which the analyzer cannot see: an attribute an author made read-only or hidden (<c>isReadOnly</c>,
        /// <c>showedOn: None</c>) opens no picker either. CodeCoverage's <c>ApiToken.CreatedByUserId</c> is that case:
        /// a settable CLR property the server stamps, referencing <c>SparkUser</c>, which has no root on purpose.
        /// </remarks>
        public void WarnUnresolved(Type entityType, PropertyInfo property, Type target, bool readOnlyInModel = false)
        {
            var writable = !readOnlyInModel
                && property.CanWrite
                && property.GetCustomAttribute<System.ComponentModel.ReadOnlyAttribute>()?.IsReadOnly != true;
            if (!writable) return;

            var name = target.Name;
            var fix = $"Add 'public IRavenQueryable<{name}> {new CollectionHelper().GetCollectionName(name)} => Session.Query<{name}>();' " +
                      $"to the context, or name a query in [Reference(typeof({name}), \"…\")].";

            if (rootsByType.TryGetValue(target, out var roots))
            {
                Console.WriteLine(
                    $"Warning: '{entityType.Name}.{property.Name}' references '{name}' without naming a query, and none of the " +
                    $"context's roots of '{name}' ({string.Join(", ", roots)}) is the plain 'Session.Query<{name}>()', so it gets " +
                    $"no query and its picker no options. {fix}");
                return;
            }

            Console.WriteLine(
                $"Warning: '{entityType.Name}.{property.Name}' references '{name}' without naming a query, and the context has " +
                $"no entity root of '{name}', so it gets no query and its picker no options (SPARK056). {fix}");
        }
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

    private static bool IsRavenQueryable(Type type)
    {
        if (!type.IsGenericType) return false;
        var genericDef = type.GetGenericTypeDefinition();
        return genericDef == typeof(IRavenQueryable<>);
    }

    private static Type? GetQueryableEntityType(Type queryableType)
    {
        if (!queryableType.IsGenericType) return null;
        return queryableType.GetGenericArguments().FirstOrDefault();
    }

    // Delegates to the shared shape definition so the generator and the startup hash check can
    // never disagree about what a property's data type is.
    private string GetDataType(Type type) => SparkModelShape.GetDataType(type);

    private static Type? GetCollectionElementType(Type type) => SparkModelShape.GetCollectionElementType(type);

    private bool IsComplexType(Type type) => SparkModelShape.IsComplexType(type);

    private bool IsNullable(Type type) => SparkModelShape.IsNullable(type);

    /// <summary>
    /// The info report D4 asks for (#467): every label key in the model that some language
    /// <c>culture.json</c> declares has no translation for, in any layer. Informational only — the
    /// runtime shows the humanized name — so it never fails synchronize or verify.
    /// </summary>
    private void ReportMissingTranslations()
    {
        var missing = DescribeMissingTranslations(hostEnvironment.ContentRootPath);
        if (missing.Count == 0) return;

        Console.WriteLine($"Info: {missing.Count} label key(s) have no translation in every declared language " +
                          $"(the humanized name is shown instead). Add them to {SparkAppData.Relative("translations.json")}:");
        foreach (var line in missing)
            Console.WriteLine($"  {line}");
    }

    internal static IReadOnlyList<string> DescribeMissingTranslations(string contentRootPath)
    {
        var cultureFile = SparkAppData.Path(contentRootPath, "culture.json");
        var languages = File.Exists(cultureFile)
            ? JsonDocument.Parse(File.ReadAllText(cultureFile)).RootElement.EnumerateObject()
                .Where(p => string.Equals(p.Name, "languages", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Array)
                .SelectMany(p => p.Value.EnumerateArray().Select(e => e.GetString() ?? ""))
                .Where(c => c.Length > 0)
                .ToList()
            : ["en"];

        var translations = SparkTranslations.Compose(contentRootPath).All;
        var keys = new List<string>();
        foreach (var file in SparkModelFiles.Compose(contentRootPath))
        {
            EntityTypeFile? model;
            try
            {
                model = JsonSerializer.Deserialize<EntityTypeFile>(file.Json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException)
            {
                continue;
            }
            if (model?.PersistentObject is not { } definition) continue;

            var prefix = $"model.{definition.Name}";
            keys.Add(definition.Label?.Key ?? $"{prefix}.label");
            keys.AddRange(definition.Attributes.Select(a => a.Label?.Key ?? $"{prefix}.attributes.{a.Name}.label"));
            keys.AddRange(definition.Tabs.Select(t => t.Label?.Key ?? $"{prefix}.tabs.{t.Name}.label"));
            keys.AddRange(definition.Groups.Select(g => g.Label?.Key ?? $"{prefix}.groups.{g.Name}.label"));
            keys.AddRange(model.Queries.Select(q => q.Label?.Key ?? $"queries.{q.Name}.label"));
        }

        var lines = new List<string>();
        foreach (var key in keys.Distinct(StringComparer.Ordinal))
        {
            translations.TryGetValue(key, out var translated);
            var absent = languages
                .Where(l => translated is null || string.IsNullOrWhiteSpace(translated[l]))
                .ToList();
            if (absent.Count > 0)
                lines.Add($"{key} [{string.Join(", ", absent)}]");
        }
        return lines;
    }

    /// <param name="querySurface">
    /// For a viewless default index, the fields its maps emit: only those can be sorted on (SP4), so the
    /// default sort is chosen among them, and is absent when none qualifies.
    /// </param>
    private string? GetDefaultSortProperty(EntityTypeDefinition entityType, IReadOnlySet<string>? querySurface = null)
    {
        var candidates = entityType.Attributes
            .Where(a => querySurface is null || querySurface.Contains(a.Name))
            .ToList();

        // Prefer Name, LastName, or first string attribute
        var sortAttr = candidates.FirstOrDefault(a => a.Name is "Name" or "LastName")
            ?? candidates.FirstOrDefault(a => a.DataType == "string");

        return sortAttr?.Name;
    }

    /// <summary>
    /// The fields a viewless default index's maps emit (<see cref="SparkMapScanner.EmittedFields"/>), or
    /// <see langword="null"/> when they cannot be read — then nothing is restricted and the author sets
    /// <c>canSort</c>/<c>canFilter</c> by hand.
    /// </summary>
    /// <remarks>
    /// Every map's fields, not only the map over this type's collection: the synchronizer has no document
    /// store to name collections with, and a field another map emits is indexed, so sorting on it is no
    /// error (this type's entries simply have no value for it).
    /// </remarks>
    internal static IReadOnlySet<string>? ViewlessQuerySurface(IndexCatalogEntry entry)
    {
        IndexDefinition definition;
        try
        {
            if (Activator.CreateInstance(entry.IndexType) is not IAbstractIndexCreationTask task) return null;
            definition = task.CreateIndexDefinition();
        }
        catch (Exception ex) when (ex is MissingMethodException or NotSupportedException or TargetInvocationException or InvalidOperationException)
        {
            Console.WriteLine($"Warning: could not read the maps of viewless default index '{entry.IndexName}' ({ex.Message}); " +
                              "its attributes keep the default sort and filter capabilities.");
            return null;
        }

        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var map in definition.Maps)
        {
            if (SparkMapScanner.EmittedFields(map) is not { } emitted)
            {
                Console.WriteLine($"Warning: could not read the fields emitted by a map of viewless default index '{entry.IndexName}'; " +
                                  "its attributes keep the default sort and filter capabilities.");
                return null;
            }
            fields.UnionWith(emitted);
        }
        return fields;
    }
}

/// <summary>
/// A model file in <c>App_Data/Model</c> could not be read, so <c>--spark-synchronize-model</c> stopped before
/// writing anything (exit 1). Regenerating around it would mint fresh ids and drop the file's hand edits.
/// </summary>
internal sealed class ModelFileUnreadableException(string message, Exception inner) : InvalidOperationException(message, inner);
