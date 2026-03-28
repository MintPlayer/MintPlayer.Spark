using System.Text.Json;
using SparkEditor.Entities;

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

    public List<PersistentObjectDefinition> LoadAllPersistentObjects()
    {
        var result = new List<PersistentObjectDefinition>();

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

    public PersistentObjectDefinition? LoadPersistentObject(string id)
    {
        // id format: "PersistentObjectDefinitions/{guid}"
        var guid = id.Replace("PersistentObjectDefinitions/", "");

        foreach (var targetPath in TargetPaths)
        {
            var modelDir = Path.Combine(targetPath, "Model");
            if (!Directory.Exists(modelDir)) continue;

            foreach (var file in Directory.GetFiles(modelDir, "*.json"))
            {
                var po = ParsePersistentObject(file);
                if (po != null && po.Id == id)
                {
                    return po;
                }
            }
        }

        return null;
    }

    public void SavePersistentObject(PersistentObjectDefinition po)
    {
        // TODO: Implement save - serialize back to the Model/*.json format
        throw new NotImplementedException("SavePersistentObject is not yet implemented.");
    }

    public void DeletePersistentObject(string id)
    {
        // TODO: Implement delete - remove the Model/*.json file
        throw new NotImplementedException("DeletePersistentObject is not yet implemented.");
    }

    private PersistentObjectDefinition? ParsePersistentObject(string filePath)
    {
        try
        {
            var json = File.ReadAllText(filePath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("persistentObject", out var poElement))
                return null;

            var po = new PersistentObjectDefinition
            {
                SourceFile = filePath,
                Name = GetStringProperty(poElement, "name") ?? Path.GetFileNameWithoutExtension(filePath),
            };

            // id -> PersistentObjectDefinitions/{guid}
            var rawId = GetStringProperty(poElement, "id");
            po.Id = rawId != null ? $"PersistentObjectDefinitions/{rawId}" : null;

            po.Description = SerializeTranslatedString(poElement, "description");
            po.Label = SerializeTranslatedString(poElement, "description"); // label defaults to description
            po.ClrType = GetStringProperty(poElement, "clrType");
            po.QueryType = GetStringProperty(poElement, "queryType");
            po.IndexName = GetStringProperty(poElement, "indexName");
            po.DisplayAttribute = GetStringProperty(poElement, "displayAttribute");
            po.Alias = GetStringProperty(poElement, "alias");
            po.Breadcrumb = GetStringProperty(poElement, "displayFormat");
            po.ContextProperty = GetStringProperty(poElement, "contextProperty");
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

    public List<AttributeDefinition> LoadAllAttributes()
    {
        var result = new List<AttributeDefinition>();

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

    public List<AttributeDefinition> LoadAttributesForPO(string poName)
    {
        return LoadAllAttributes()
            .Where(a => string.Equals(a.PersistentObjectName, poName, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private List<AttributeDefinition> ParseAttributes(string filePath)
    {
        var result = new List<AttributeDefinition>();

        try
        {
            var json = File.ReadAllText(filePath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("persistentObject", out var poElement))
                return result;

            var poName = GetStringProperty(poElement, "name") ?? Path.GetFileNameWithoutExtension(filePath);
            var poId = GetStringProperty(poElement, "id");

            if (!poElement.TryGetProperty("attributes", out var attrsElement) || attrsElement.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var attrElement in attrsElement.EnumerateArray())
            {
                var attr = new AttributeDefinition
                {
                    PersistentObjectName = poName,
                    PersistentObjectId = poId != null ? $"PersistentObjectDefinitions/{poId}" : null,
                    Name = GetStringProperty(attrElement, "name") ?? string.Empty,
                    Label = SerializeTranslatedString(attrElement, "label"),
                    DataType = GetStringProperty(attrElement, "dataType") ?? "string",
                    IsRequired = GetBoolProperty(attrElement, "isRequired"),
                    IsVisible = GetBoolProperty(attrElement, "isVisible", defaultValue: true),
                    IsReadOnly = GetBoolProperty(attrElement, "isReadOnly"),
                    Order = GetIntProperty(attrElement, "order"),
                    ShowedOn = GetStringProperty(attrElement, "showedOn"),
                    Group = GetStringProperty(attrElement, "group"),
                    ColumnSpan = GetIntProperty(attrElement, "columnSpan", defaultValue: 1),
                    Renderer = GetStringProperty(attrElement, "renderer"),
                    ReferenceType = GetStringProperty(attrElement, "referenceType"),
                    AsDetailType = GetStringProperty(attrElement, "asDetailType"),
                    IsArray = GetBoolProperty(attrElement, "isArray"),
                    EditMode = GetStringProperty(attrElement, "editMode"),
                    LookupReferenceType = GetStringProperty(attrElement, "lookupReferenceType"),
                };

                // id -> AttributeDefinitions/{guid}
                var rawId = GetStringProperty(attrElement, "id");
                attr.Id = rawId != null ? $"AttributeDefinitions/{rawId}" : null;

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

    public List<QueryDefinition> LoadAllQueries()
    {
        var result = new List<QueryDefinition>();

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

    public List<QueryDefinition> LoadQueriesForPO(string poName)
    {
        return LoadAllQueries()
            .Where(q => string.Equals(q.PersistentObjectName, poName, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private List<QueryDefinition> ParseQueries(string filePath)
    {
        var result = new List<QueryDefinition>();

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
                var query = new QueryDefinition
                {
                    PersistentObjectName = poName,
                    Name = GetStringProperty(queryElement, "name") ?? string.Empty,
                    Description = SerializeTranslatedString(queryElement, "description"),
                    Label = SerializeTranslatedString(queryElement, "description"),
                    Source = GetStringProperty(queryElement, "source"),
                    Alias = GetStringProperty(queryElement, "alias"),
                    EntityType = GetStringProperty(queryElement, "entityType"),
                    IsHidden = GetBoolProperty(queryElement, "isHidden"),
                    RenderMode = GetStringProperty(queryElement, "renderMode"),
                };

                // id -> QueryDefinitions/{guid}
                var rawId = GetStringProperty(queryElement, "id");
                query.Id = rawId != null ? $"QueryDefinitions/{rawId}" : null;

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

    public List<CustomActionDef> LoadAllCustomActions()
    {
        var result = new List<CustomActionDef>();

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
                    var action = new CustomActionDef
                    {
                        Id = $"CustomActionDefs/{prop.Name}",
                        Name = prop.Name,
                        DisplayName = SerializeTranslatedString(actionElement, "displayName"),
                        Icon = GetStringProperty(actionElement, "icon"),
                        Description = GetStringProperty(actionElement, "description"),
                        ShowedOn = GetStringProperty(actionElement, "showedOn"),
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

    public void SaveCustomAction(string name, CustomActionDef action)
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

    public List<ProgramUnitGroupDef> LoadAllProgramUnitGroups()
    {
        var result = new List<ProgramUnitGroupDef>();

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
                    var rawId = GetStringProperty(groupElement, "id");
                    var group = new ProgramUnitGroupDef
                    {
                        Id = rawId != null ? $"ProgramUnitGroupDefs/{rawId}" : null,
                        Name = SerializeTranslatedString(groupElement, "name"),
                        Icon = GetStringProperty(groupElement, "icon"),
                        Order = GetIntProperty(groupElement, "order"),
                    };

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

    public List<ProgramUnitDef> LoadAllProgramUnits()
    {
        var result = new List<ProgramUnitDef>();

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
                    var groupId = GetStringProperty(groupElement, "id");
                    var groupIdPrefixed = groupId != null ? $"ProgramUnitGroupDefs/{groupId}" : null;

                    if (!groupElement.TryGetProperty("programUnits", out var unitsElement) || unitsElement.ValueKind != JsonValueKind.Array)
                        continue;

                    foreach (var unitElement in unitsElement.EnumerateArray())
                    {
                        var rawId = GetStringProperty(unitElement, "id");
                        var unit = new ProgramUnitDef
                        {
                            Id = rawId != null ? $"ProgramUnitDefs/{rawId}" : null,
                            Name = SerializeTranslatedString(unitElement, "name"),
                            Icon = GetStringProperty(unitElement, "icon"),
                            Type = GetStringProperty(unitElement, "type") ?? "query",
                            QueryId = GetStringProperty(unitElement, "queryId"),
                            PersistentObjectId = GetStringProperty(unitElement, "persistentObjectId"),
                            Order = GetIntProperty(unitElement, "order"),
                            Alias = GetStringProperty(unitElement, "alias"),
                            GroupId = groupIdPrefixed,
                        };

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

    public List<SecurityGroupDef> LoadAllSecurityGroups()
    {
        var result = new List<SecurityGroupDef>();

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
                    var group = new SecurityGroupDef
                    {
                        Id = $"SecurityGroupDefs/{prop.Name}",
                        Name = prop.Value.ValueKind == JsonValueKind.Object
                            ? prop.Value.GetRawText()
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

    public List<SecurityRightDef> LoadAllSecurityRights()
    {
        var result = new List<SecurityRightDef>();

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
                    var right = new SecurityRightDef
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

    public List<LanguageDef> LoadAllLanguages()
    {
        var result = new List<LanguageDef>();

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
                    var lang = new LanguageDef
                    {
                        Id = $"LanguageDefs/{prop.Name}",
                        Culture = prop.Name,
                        Name = prop.Value.ValueKind == JsonValueKind.Object
                            ? prop.Value.GetRawText()
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

    public List<TranslationDef> LoadAllTranslations()
    {
        var result = new List<TranslationDef>();

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
                    var translation = new TranslationDef
                    {
                        Id = $"TranslationDefs/{prop.Name}",
                        Key = prop.Name,
                        Values = prop.Value.ValueKind == JsonValueKind.Object
                            ? prop.Value.GetRawText()
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

    /// <summary>
    /// Serializes a TranslatedString (dictionary object) property to its JSON string representation.
    /// Returns null if the property doesn't exist or isn't an object.
    /// </summary>
    private static string? SerializeTranslatedString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Object)
        {
            return prop.GetRawText();
        }
        return null;
    }

    #endregion
}
