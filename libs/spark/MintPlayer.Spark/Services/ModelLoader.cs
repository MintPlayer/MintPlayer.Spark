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
                    ResolveText(entityType, entityTypeFile.Queries);

                    // Auto-generate alias from Name if not explicitly set
                    entityType.Alias ??= entityType.Name.ToLowerInvariant();

                    // Symmetrical with SparkQueryAliasIndex.Index, which throws for the same reason
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
            catch (JsonException ex) when (ex.Path?.Contains("triggersRefresh", StringComparison.OrdinalIgnoreCase) == true)
            {
                // The boolean form was removed when TriggersRefresh became ERefreshTrigger. Degrading
                // to a message here would drop the whole type and leave it unroutable, so a stale
                // `"triggersRefresh": true` must stop the process and say how to fix it.
                throw new InvalidOperationException(
                    $"Model file {file}: '{ex.Path}' must be one of \"None\", \"Auto\", \"ValueChanged\" or \"Blur\". " +
                    "The boolean form was removed; replace true with \"Auto\".", ex);
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

    /// <summary>
    /// Replaces every key a model file holds (or the absence of one) with its text (#467, D1/D4/D6).
    /// Done once here, so everything downstream (the wire, validation messages, breadcrumbs) keeps
    /// seeing a resolved <see cref="TranslatedString"/>.
    /// </summary>
    internal static void ResolveText(EntityTypeDefinition entityType, IEnumerable<SparkQuery> queries)
    {
        var prefix = $"model.{entityType.Name}";
        entityType.Label = SparkText.Resolve(entityType.Label, $"{prefix}.label", entityType.Name);

        foreach (var attribute in entityType.Attributes)
        {
            var attributePrefix = $"{prefix}.attributes.{attribute.Name}";
            attribute.Label = SparkText.Resolve(attribute.Label, $"{attributePrefix}.label", attribute.Name);
            // Help text has no fallback: an attribute without one shows no [i].
            attribute.Description = SparkText.Resolve(attribute.Description, $"{attributePrefix}.description", fallbackName: null);

            // A rule's custom message is an explicit key (conventionally {attr}.rules.{type}); an
            // untranslated one leaves the built-in validation.* message in charge.
            foreach (var rule in attribute.Rules)
            {
                var ruleKey = $"{attributePrefix}.rules.{rule.Type}";
                SparkText.RejectInlineText(rule.Message, ruleKey);
                rule.Message = rule.Message?.Key is { } key ? SparkText.Lookup(key) : null;
            }
        }

        foreach (var tab in entityType.Tabs)
            tab.Label = SparkText.Resolve(tab.Label, $"{prefix}.tabs.{tab.Name}.label", tab.Name);

        foreach (var group in entityType.Groups)
            group.Label = SparkText.Resolve(group.Label, $"{prefix}.groups.{group.Name}.label", group.Name);

        foreach (var query in queries)
            query.Label = SparkText.Resolve(query.Label, $"queries.{query.Name}.label", query.Name);
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
