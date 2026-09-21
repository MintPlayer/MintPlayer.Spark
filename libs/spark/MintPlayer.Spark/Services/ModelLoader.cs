using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using System.Text.Json;

namespace MintPlayer.Spark.Services;

public interface IModelLoader
{
    IEnumerable<EntityTypeDefinition> GetEntityTypes();
    EntityTypeDefinition? GetEntityType(Guid id);
    EntityTypeDefinition? GetEntityTypeByClrType(string clrType);
    EntityTypeDefinition? GetEntityTypeByName(string name);
    EntityTypeDefinition? GetEntityTypeByAlias(string alias);
    EntityTypeDefinition? ResolveEntityType(string idOrAlias);
    IEnumerable<SparkQuery> GetQueries();
}

[Register(typeof(IModelLoader), ServiceLifetime.Singleton)]
internal partial class ModelLoader : IModelLoader
{
    [Inject] private readonly IHostEnvironment hostEnvironment;

    private Lazy<(Dictionary<Guid, EntityTypeDefinition> ById, Dictionary<string, EntityTypeDefinition> ByAlias, List<SparkQuery> Queries)>? _data;

    private (Dictionary<Guid, EntityTypeDefinition> ById, Dictionary<string, EntityTypeDefinition> ByAlias, List<SparkQuery> Queries) Data
    {
        get
        {
            _data ??= new Lazy<(Dictionary<Guid, EntityTypeDefinition>, Dictionary<string, EntityTypeDefinition>, List<SparkQuery>)>(LoadData);
            return _data.Value;
        }
    }

    private (Dictionary<Guid, EntityTypeDefinition>, Dictionary<string, EntityTypeDefinition>, List<SparkQuery>) LoadData()
    {
        var byId = new Dictionary<Guid, EntityTypeDefinition>();
        var byAlias = new Dictionary<string, EntityTypeDefinition>(StringComparer.OrdinalIgnoreCase);
        var allQueries = new List<SparkQuery>();
        var modelPath = Path.Combine(hostEnvironment.ContentRootPath, "App_Data", "Model");

        if (!Directory.Exists(modelPath))
            return (byId, byAlias, allQueries);

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

                    // Auto-generate alias from Name if not explicitly set
                    entityType.Alias ??= entityType.Name.ToLowerInvariant();

                    // Symmetrical with SparkQueryAliases.Index, which throws for the same reason
                    // (#327 M3). This used to warn to the console and keep the FIRST, while the
                    // line above kept the LAST — so on a collision the two indexes resolved the same
                    // alias to different types, and the loser was silently unroutable. A URL names
                    // exactly one type; there is no useful behaviour to pick between them.
                    if (byAlias.TryGetValue(entityType.Alias, out var existing))
                    {
                        throw new InvalidOperationException(
                            $"Two entity types resolve to the alias '{entityType.Alias}': '{existing.Name}' and " +
                            $"'{entityType.Name}' (in {Path.GetFileName(file)}). A URL identifies exactly one type, " +
                            $"so the second would be unreachable by alias. Give one of them an explicit, distinct " +
                            $"\"alias\" in its model file.");
                    }

                    byId[entityType.Id] = entityType;
                    byAlias[entityType.Alias] = entityType;

                    // Extract queries and auto-populate EntityType
                    foreach (var query in entityTypeFile.Queries)
                    {
                        query.EntityType ??= entityType.Name;
                        allQueries.Add(query);
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // Narrow on purpose. This used to catch everything, which quietly defeated the
                // alias-collision throw above (#327 M3): the exception was raised, printed as
                // "Error loading model file …", and swallowed — so the colliding type was skipped
                // and the application started anyway, with one of two types unroutable. A file that
                // cannot be read or parsed is a different kind of problem and still degrades to a
                // message; a model that parses but contradicts itself must stop the process.
                Console.WriteLine($"Error loading model file {file}: {ex.Message}");
            }
        }

        return (byId, byAlias, allQueries);
    }

    public IEnumerable<EntityTypeDefinition> GetEntityTypes()
        => Data.ById.Values;

    public EntityTypeDefinition? GetEntityType(Guid id)
        => Data.ById.TryGetValue(id, out var entityType) ? entityType : null;

    public EntityTypeDefinition? GetEntityTypeByClrType(string clrType)
        => Data.ById.Values.FirstOrDefault(e => e.ClrType == clrType);

    public EntityTypeDefinition? GetEntityTypeByName(string name)
        => Data.ById.Values.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    public EntityTypeDefinition? GetEntityTypeByAlias(string alias)
        => Data.ByAlias.TryGetValue(alias, out var entityType) ? entityType : null;

    /// <summary>
    /// Resolves a type from whatever the wire happens to carry: its id, its alias, or its name.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>The name fallback is load-bearing, not politeness.</b> A sub-query request sends
    /// <c>parentType</c>, and the client takes that from the parent persistent object's
    /// <c>name</c> — while this used to resolve by alias only. That works for every type whose
    /// alias is just its lowercased name, which is every generated type, and silently fails for
    /// one that declares an alias of its own: the parent resolves to null and the endpoint answers
    /// <c>404 "Parent not found"</c>, which reads as a missing document rather than as a name that
    /// was never looked up.
    /// <para>
    /// Alias first, because that is what a URL carries and what a deliberate rename means. A name
    /// collision with another type's alias would therefore resolve to the alias owner — correct,
    /// since the alias is the addressable form.
    /// </para>
    /// </remarks>
    public EntityTypeDefinition? ResolveEntityType(string idOrAlias)
    {
        if (Guid.TryParse(idOrAlias, out var guid))
            return GetEntityType(guid);

        return GetEntityTypeByAlias(idOrAlias) ?? GetEntityTypeByName(idOrAlias);
    }

    public IEnumerable<SparkQuery> GetQueries()
        => Data.Queries;
}
