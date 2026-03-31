using System.Text.Json;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Authorization.Models;
using MintPlayer.Spark.Models;

namespace SparkEditor.Services;

public class SparkEditorFileService : ISparkEditorFileService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public SparkEditorFileService(IReadOnlyList<string> targetPaths)
    {
        TargetPaths = targetPaths;
    }

    public IReadOnlyList<string> TargetPaths { get; }

    #region Persistent Objects

    public List<EntityTypeDefinition> LoadAllPersistentObjects()
    {
        var result = new List<EntityTypeDefinition>();

        foreach (var targetPath in TargetPaths)
        {
            var modelDir = Path.Combine(targetPath, "Model");
            if (!Directory.Exists(modelDir)) continue;

            foreach (var file in Directory.GetFiles(modelDir, "*.json"))
            {
                var po = ParsePersistentObject(file);
                if (po != null)
                {
                    result.Add(po);
                }
            }
        }

        return result;
    }

    public EntityTypeDefinition? LoadPersistentObject(string id)
    {
        foreach (var targetPath in TargetPaths)
        {
            var modelDir = Path.Combine(targetPath, "Model");
            if (!Directory.Exists(modelDir)) continue;

            foreach (var file in Directory.GetFiles(modelDir, "*.json"))
            {
                var po = ParsePersistentObject(file);
                if (po != null && po.Id.ToString() == id)
                {
                    return po;
                }
            }
        }

        return null;
    }

    public void SavePersistentObject(EntityTypeDefinition po)
    {
        // TODO: Implement save - serialize back to the Model/*.json format
        throw new NotImplementedException("SavePersistentObject is not yet implemented.");
    }

    public void DeletePersistentObject(string id)
    {
        // TODO: Implement delete - remove the Model/*.json file
        throw new NotImplementedException("DeletePersistentObject is not yet implemented.");
    }

    private EntityTypeDefinition? ParsePersistentObject(string filePath)
    {
        try
        {
            var json = File.ReadAllText(filePath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("persistentObject", out var poElement))
                return null;

            var po = new EntityTypeDefinition
            {
                SourceFile = filePath,
                Name = GetStringProperty(poElement, "name") ?? Path.GetFileNameWithoutExtension(filePath),
            };

            if (TryGetGuid(poElement, "id", out var id))
                po.Id = id;

            po.Description = DeserializeTranslatedString(poElement, "description");
            po.ClrType = GetStringProperty(poElement, "clrType");
            po.QueryType = GetStringProperty(poElement, "queryType");
            po.IndexName = GetStringProperty(poElement, "indexName");
            po.DisplayAttribute = GetStringProperty(poElement, "displayAttribute");
            po.Alias = GetStringProperty(poElement, "alias");
            po.DisplayFormat = GetStringProperty(poElement, "displayFormat");
            po.IsReadOnly = GetBoolProperty(poElement, "isReadOnly");
            po.IsHidden = GetBoolProperty(poElement, "isHidden");

            return po;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Failed to parse model file '{filePath}': {ex.Message}");
            return null;
        }
    }

    #endregion

    #region Attributes

    public List<EntityAttributeDefinition> LoadAllAttributes()
    {
        var result = new List<EntityAttributeDefinition>();

        foreach (var targetPath in TargetPaths)
        {
            var modelDir = Path.Combine(targetPath, "Model");
            if (!Directory.Exists(modelDir)) continue;

            foreach (var file in Directory.GetFiles(modelDir, "*.json"))
            {
                result.AddRange(ParseAttributes(file));
            }
        }

        return result;
    }

    public List<EntityAttributeDefinition> LoadAttributesForPO(string poName)
    {
        return LoadAllAttributes()
            .Where(a => string.Equals(a.PersistentObjectName, poName, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private List<EntityAttributeDefinition> ParseAttributes(string filePath)
    {
        var result = new List<EntityAttributeDefinition>();

        try
        {
            var json = File.ReadAllText(filePath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("persistentObject", out var poElement))
                return result;

            var poName = GetStringProperty(poElement, "name") ?? Path.GetFileNameWithoutExtension(filePath);

            if (!poElement.TryGetProperty("attributes", out var attrsElement) || attrsElement.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var attrElement in attrsElement.EnumerateArray())
            {
                var attr = new EntityAttributeDefinition
                {
                    PersistentObjectName = poName,
                    Name = GetStringProperty(attrElement, "name") ?? string.Empty,
                    Label = DeserializeTranslatedString(attrElement, "label"),
                    DataType = GetStringProperty(attrElement, "dataType") ?? "string",
                    IsRequired = GetBoolProperty(attrElement, "isRequired"),
                    IsVisible = GetBoolProperty(attrElement, "isVisible", defaultValue: true),
                    IsReadOnly = GetBoolProperty(attrElement, "isReadOnly"),
                    Order = GetIntProperty(attrElement, "order"),
                    Renderer = GetStringProperty(attrElement, "renderer"),
                    ReferenceType = GetStringProperty(attrElement, "referenceType"),
                    AsDetailType = GetStringProperty(attrElement, "asDetailType"),
                    IsArray = GetBoolProperty(attrElement, "isArray"),
                    EditMode = GetStringProperty(attrElement, "editMode"),
                    LookupReferenceType = GetStringProperty(attrElement, "lookupReferenceType"),
                };

                if (TryGetGuid(attrElement, "id", out var attrId))
                    attr.Id = attrId;

                result.Add(attr);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Failed to parse attributes from '{filePath}': {ex.Message}");
        }

        return result;
    }

    #endregion

    #region Queries

    public List<SparkQuery> LoadAllQueries()
    {
        var result = new List<SparkQuery>();

        foreach (var targetPath in TargetPaths)
        {
            var modelDir = Path.Combine(targetPath, "Model");
            if (!Directory.Exists(modelDir)) continue;

            foreach (var file in Directory.GetFiles(modelDir, "*.json"))
            {
                result.AddRange(ParseQueries(file));
            }
        }

        return result;
    }

    public List<SparkQuery> LoadQueriesForPO(string poName)
    {
        return LoadAllQueries()
            .Where(q => string.Equals(q.PersistentObjectName, poName, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private List<SparkQuery> ParseQueries(string filePath)
    {
        var result = new List<SparkQuery>();

        try
        {
            var json = File.ReadAllText(filePath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Get PO name for back-reference
            string? poName = null;
            if (root.TryGetProperty("persistentObject", out var poElement))
            {
                poName = GetStringProperty(poElement, "name");
            }

            if (!root.TryGetProperty("queries", out var queriesElement) || queriesElement.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var queryElement in queriesElement.EnumerateArray())
            {
                var query = new SparkQuery
                {
                    PersistentObjectName = poName,
                    Name = GetStringProperty(queryElement, "name") ?? string.Empty,
                    Description = DeserializeTranslatedString(queryElement, "description"),
                    Source = GetStringProperty(queryElement, "source"),
                    Alias = GetStringProperty(queryElement, "alias"),
                    EntityType = GetStringProperty(queryElement, "entityType"),
                };

                if (TryGetGuid(queryElement, "id", out var queryId))
                    query.Id = queryId;

                result.Add(query);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Failed to parse queries from '{filePath}': {ex.Message}");
        }

        return result;
    }

    #endregion

    #region Custom Actions

    public List<CustomActionDefinition> LoadAllCustomActions()
    {
        var result = new List<CustomActionDefinition>();

        foreach (var targetPath in TargetPaths)
        {
            var filePath = Path.Combine(targetPath, "customActions.json");
            if (!File.Exists(filePath)) continue;

            try
            {
                var json = File.ReadAllText(filePath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                foreach (var prop in root.EnumerateObject())
                {
                    var actionElement = prop.Value;
                    var action = new CustomActionDefinition
                    {
                        Id = $"CustomActionDefs/{prop.Name}",
                        Name = prop.Name,
                        DisplayName = DeserializeTranslatedString(actionElement, "displayName"),
                        Icon = GetStringProperty(actionElement, "icon"),
                        Description = GetStringProperty(actionElement, "description"),
                        ShowedOn = GetStringProperty(actionElement, "showedOn") ?? "both",
                        SelectionRule = GetStringProperty(actionElement, "selectionRule"),
                        RefreshOnCompleted = GetBoolProperty(actionElement, "refreshOnCompleted"),
                        ConfirmationMessageKey = GetStringProperty(actionElement, "confirmationMessageKey"),
                        Offset = GetIntProperty(actionElement, "offset"),
                    };

                    result.Add(action);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Failed to parse custom actions from '{filePath}': {ex.Message}");
            }
        }

        return result;
    }

    public void SaveCustomAction(string name, CustomActionDefinition action)
    {
        // TODO: Implement save - update the customActions.json file
        throw new NotImplementedException("SaveCustomAction is not yet implemented.");
    }

    public void DeleteCustomAction(string name)
    {
        // TODO: Implement delete - remove entry from customActions.json
        throw new NotImplementedException("DeleteCustomAction is not yet implemented.");
    }

    #endregion

    #region Program Units

    public List<ProgramUnitGroup> LoadAllProgramUnitGroups()
    {
        var result = new List<ProgramUnitGroup>();

        foreach (var targetPath in TargetPaths)
        {
            var filePath = Path.Combine(targetPath, "programUnits.json");
            if (!File.Exists(filePath)) continue;

            try
            {
                var json = File.ReadAllText(filePath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("programUnitGroups", out var groupsElement) || groupsElement.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var groupElement in groupsElement.EnumerateArray())
                {
                    var group = new ProgramUnitGroup
                    {
                        Name = DeserializeTranslatedString(groupElement, "name"),
                        Icon = GetStringProperty(groupElement, "icon"),
                        Order = GetIntProperty(groupElement, "order"),
                    };

                    if (TryGetGuid(groupElement, "id", out var groupId))
                        group.Id = groupId;

                    result.Add(group);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Failed to parse program unit groups from '{filePath}': {ex.Message}");
            }
        }

        return result;
    }

    public List<ProgramUnit> LoadAllProgramUnits()
    {
        var result = new List<ProgramUnit>();

        foreach (var targetPath in TargetPaths)
        {
            var filePath = Path.Combine(targetPath, "programUnits.json");
            if (!File.Exists(filePath)) continue;

            try
            {
                var json = File.ReadAllText(filePath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("programUnitGroups", out var groupsElement) || groupsElement.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var groupElement in groupsElement.EnumerateArray())
                {
                    Guid? groupId = null;
                    if (TryGetGuid(groupElement, "id", out var gid))
                        groupId = gid;

                    if (!groupElement.TryGetProperty("programUnits", out var unitsElement) || unitsElement.ValueKind != JsonValueKind.Array)
                        continue;

                    foreach (var unitElement in unitsElement.EnumerateArray())
                    {
                        var unit = new ProgramUnit
                        {
                            Name = DeserializeTranslatedString(unitElement, "name"),
                            Icon = GetStringProperty(unitElement, "icon"),
                            Type = GetStringProperty(unitElement, "type") ?? "query",
                            Order = GetIntProperty(unitElement, "order"),
                            Alias = GetStringProperty(unitElement, "alias"),
                            GroupId = groupId,
                        };

                        if (TryGetGuid(unitElement, "id", out var unitId))
                            unit.Id = unitId;
                        if (TryGetGuid(unitElement, "queryId", out var qid))
                            unit.QueryId = qid;
                        if (TryGetGuid(unitElement, "persistentObjectId", out var poid))
                            unit.PersistentObjectId = poid;

                        result.Add(unit);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Failed to parse program units from '{filePath}': {ex.Message}");
            }
        }

        return result;
    }

    #endregion

    #region Security

    public List<SecurityGroupDefinition> LoadAllSecurityGroups()
    {
        var result = new List<SecurityGroupDefinition>();

        foreach (var targetPath in TargetPaths)
        {
            var filePath = Path.Combine(targetPath, "security.json");
            if (!File.Exists(filePath)) continue;

            try
            {
                var json = File.ReadAllText(filePath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("groups", out var groupsElement) || groupsElement.ValueKind != JsonValueKind.Object)
                    continue;

                foreach (var prop in groupsElement.EnumerateObject())
                {
                    var group = new SecurityGroupDefinition
                    {
                        Id = $"SecurityGroupDefs/{prop.Name}",
                        Name = prop.Value.ValueKind == JsonValueKind.Object
                            ? JsonSerializer.Deserialize<TranslatedString>(prop.Value.GetRawText(), JsonOptions)
                            : null,
                    };

                    result.Add(group);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Failed to parse security groups from '{filePath}': {ex.Message}");
            }
        }

        return result;
    }

    public List<Right> LoadAllSecurityRights()
    {
        var result = new List<Right>();

        foreach (var targetPath in TargetPaths)
        {
            var filePath = Path.Combine(targetPath, "security.json");
            if (!File.Exists(filePath)) continue;

            try
            {
                var json = File.ReadAllText(filePath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("rights", out var rightsElement) || rightsElement.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var rightElement in rightsElement.EnumerateArray())
                {
                    var rawId = GetStringProperty(rightElement, "id");
                    var right = new Right
                    {
                        Id = rawId != null ? $"SecurityRightDefs/{rawId}" : null,
                        Resource = GetStringProperty(rightElement, "resource") ?? string.Empty,
                        GroupId = GetStringProperty(rightElement, "groupId"),
                        IsDenied = GetBoolProperty(rightElement, "isDenied"),
                        IsImportant = GetBoolProperty(rightElement, "isImportant"),
                    };

                    result.Add(right);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Failed to parse security rights from '{filePath}': {ex.Message}");
            }
        }

        return result;
    }

    #endregion

    #region Culture

    public List<LanguageDefinition> LoadAllLanguages()
    {
        var result = new List<LanguageDefinition>();

        foreach (var targetPath in TargetPaths)
        {
            var filePath = Path.Combine(targetPath, "culture.json");
            if (!File.Exists(filePath)) continue;

            try
            {
                var json = File.ReadAllText(filePath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("languages", out var langsElement) || langsElement.ValueKind != JsonValueKind.Object)
                    continue;

                foreach (var prop in langsElement.EnumerateObject())
                {
                    var lang = new LanguageDefinition
                    {
                        Id = $"LanguageDefs/{prop.Name}",
                        Culture = prop.Name,
                        Name = prop.Value.ValueKind == JsonValueKind.Object
                            ? JsonSerializer.Deserialize<TranslatedString>(prop.Value.GetRawText(), JsonOptions)
                            : null,
                    };

                    result.Add(lang);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Failed to parse languages from '{filePath}': {ex.Message}");
            }
        }

        return result;
    }

    #endregion

    #region Translations

    public List<TranslationEntry> LoadAllTranslations()
    {
        var result = new List<TranslationEntry>();

        foreach (var targetPath in TargetPaths)
        {
            var filePath = Path.Combine(targetPath, "translations.json");
            if (!File.Exists(filePath)) continue;

            try
            {
                var json = File.ReadAllText(filePath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                foreach (var prop in root.EnumerateObject())
                {
                    var translation = new TranslationEntry
                    {
                        Id = $"TranslationDefs/{prop.Name}",
                        Key = prop.Name,
                        Values = prop.Value.ValueKind == JsonValueKind.Object
                            ? JsonSerializer.Deserialize<TranslatedString>(prop.Value.GetRawText(), JsonOptions)
                            : null,
                    };

                    result.Add(translation);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Failed to parse translations from '{filePath}': {ex.Message}");
            }
        }

        return result;
    }

    #endregion

    #region JSON Helpers

    private static string? GetStringProperty(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }
        return null;
    }

    private static bool GetBoolProperty(JsonElement element, string propertyName, bool defaultValue = false)
    {
        if (element.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.True) return true;
            if (prop.ValueKind == JsonValueKind.False) return false;
        }
        return defaultValue;
    }

    private static int GetIntProperty(JsonElement element, string propertyName, int defaultValue = 0)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number)
        {
            return prop.GetInt32();
        }
        return defaultValue;
    }

    private static bool TryGetGuid(JsonElement element, string propertyName, out Guid result)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            return Guid.TryParse(prop.GetString(), out result);
        }
        result = default;
        return false;
    }

    /// <summary>
    /// Deserializes a TranslatedString (dictionary object) property.
    /// Returns null if the property doesn't exist or isn't an object.
    /// </summary>
    private static TranslatedString? DeserializeTranslatedString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Object)
        {
            return JsonSerializer.Deserialize<TranslatedString>(prop.GetRawText(), JsonOptions);
        }
        return null;
    }

    #endregion
}
