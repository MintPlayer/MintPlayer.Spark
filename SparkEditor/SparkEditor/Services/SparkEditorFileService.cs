using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Authorization.Models;
using MintPlayer.Spark.Models;

namespace SparkEditor.Services;

public class SparkEditorFileService : ISparkEditorFileService, IDisposable
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonDocumentOptions DocOptions = new() { AllowTrailingCommas = true };

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _debounceTokens = new();
    private volatile bool _isWriting;

    public SparkEditorFileService(IReadOnlyList<string> targetPaths)
    {
        TargetPaths = targetPaths;
    }

    public IReadOnlyList<string> TargetPaths { get; }

    public event EventHandler<FileChangedEventArgs>? FileChanged;

    // ═══════════════════════════════════════════════════════════════
    // Persistent Objects
    // ═══════════════════════════════════════════════════════════════

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
        var filePath = po.SourceFile
            ?? Path.Combine(TargetPaths[0], "Model", $"{po.Name}.json");

        // Ensure Model directory exists
        var dir = Path.GetDirectoryName(filePath)!;
        Directory.CreateDirectory(dir);

        JsonObject root;
        if (File.Exists(filePath))
        {
            var existing = File.ReadAllText(filePath);
            root = JsonNode.Parse(existing, NodeOptions, DocOptions)?.AsObject() ?? new JsonObject();
        }
        else
        {
            root = new JsonObject();
            if (po.Id == Guid.Empty) po.Id = Guid.NewGuid();
        }

        // Preserve existing embedded arrays (attributes, tabs, groups) from the file.
        // SavePersistentObject only updates PO-level metadata properties.
        var existingPo = root["persistentObject"]?.AsObject();
        var poNode = BuildPersistentObjectNode(po);

        if (existingPo != null)
        {
            // Carry over embedded arrays that are managed by their own Save methods
            foreach (var arrayKey in new[] { "tabs", "groups", "attributes" })
            {
                if (existingPo[arrayKey] is JsonNode existingArray)
                {
                    var cloned = JsonNode.Parse(existingArray.ToJsonString());
                    poNode[arrayKey] = cloned;
                }
            }
        }

        root["persistentObject"] = poNode;

        // Preserve existing queries array if present, otherwise create empty
        if (root["queries"] == null)
            root["queries"] = new JsonArray();

        WriteJsonFile(filePath, root);
    }

    public void DeletePersistentObject(string id)
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
                    DeleteFileWithSuppression(file);
                    return;
                }
            }
        }
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

    // ═══════════════════════════════════════════════════════════════
    // Attributes
    // ═══════════════════════════════════════════════════════════════

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

    public void SaveAttribute(string poName, EntityAttributeDefinition attr)
    {
        var filePath = FindModelFileForPO(poName)
            ?? throw new InvalidOperationException($"No model file found for PO '{poName}'");

        var json = File.ReadAllText(filePath);
        var root = JsonNode.Parse(json, NodeOptions, DocOptions)!.AsObject();

        var poNode = root["persistentObject"]?.AsObject()
            ?? throw new InvalidOperationException($"No persistentObject in '{filePath}'");

        var attrsArray = poNode["attributes"]?.AsArray() ?? new JsonArray();

        if (attr.Id == Guid.Empty) attr.Id = Guid.NewGuid();

        // Find existing by id, or append
        var idStr = attr.Id.ToString();
        var existingIndex = FindIndexById(attrsArray, idStr);
        var node = BuildAttributeNode(attr);

        if (existingIndex >= 0)
        {
            attrsArray[existingIndex] = node;
        }
        else
        {
            // New items are appended at the end, preserving existing order
            attrsArray.Add(node);
        }

        poNode["attributes"] = attrsArray;
        WriteJsonFile(filePath, root);
    }

    public void DeleteAttribute(string poName, string attributeId)
    {
        var filePath = FindModelFileForPO(poName)
            ?? throw new InvalidOperationException($"No model file found for PO '{poName}'");

        var json = File.ReadAllText(filePath);
        var root = JsonNode.Parse(json, NodeOptions, DocOptions)!.AsObject();

        var poNode = root["persistentObject"]?.AsObject();
        var attrsArray = poNode?["attributes"]?.AsArray();
        if (attrsArray == null) return;

        var index = FindIndexById(attrsArray, attributeId);
        if (index >= 0)
        {
            attrsArray.RemoveAt(index);
            WriteJsonFile(filePath, root);
        }
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

    // ═══════════════════════════════════════════════════════════════
    // Queries
    // ═══════════════════════════════════════════════════════════════

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

    public void SaveQuery(string poName, SparkQuery query)
    {
        var filePath = FindModelFileForPO(poName)
            ?? throw new InvalidOperationException($"No model file found for PO '{poName}'");

        var json = File.ReadAllText(filePath);
        var root = JsonNode.Parse(json, NodeOptions, DocOptions)!.AsObject();

        var queriesArray = root["queries"]?.AsArray() ?? new JsonArray();

        if (query.Id == Guid.Empty) query.Id = Guid.NewGuid();

        var idStr = query.Id.ToString();
        var existingIndex = FindIndexById(queriesArray, idStr);
        var node = BuildQueryNode(query);

        if (existingIndex >= 0)
        {
            queriesArray[existingIndex] = node;
        }
        else
        {
            queriesArray.Add(node);
        }

        root["queries"] = queriesArray;
        WriteJsonFile(filePath, root);
    }

    public void DeleteQuery(string poName, string queryId)
    {
        var filePath = FindModelFileForPO(poName)
            ?? throw new InvalidOperationException($"No model file found for PO '{poName}'");

        var json = File.ReadAllText(filePath);
        var root = JsonNode.Parse(json, NodeOptions, DocOptions)!.AsObject();

        var queriesArray = root["queries"]?.AsArray();
        if (queriesArray == null) return;

        var index = FindIndexById(queriesArray, queryId);
        if (index >= 0)
        {
            queriesArray.RemoveAt(index);
            WriteJsonFile(filePath, root);
        }
    }

    private List<SparkQuery> ParseQueries(string filePath)
    {
        var result = new List<SparkQuery>();

        try
        {
            var json = File.ReadAllText(filePath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

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

    // ═══════════════════════════════════════════════════════════════
    // Custom Actions
    // ═══════════════════════════════════════════════════════════════

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
        var filePath = FindSingleFile("customActions.json");
        var root = LoadOrCreateJsonObject(filePath);

        root[name] = BuildCustomActionNode(action);

        // Existing key order is preserved by JsonObject (insertion order).
        // New keys are naturally appended at the end.
        WriteJsonFile(filePath, root);
    }

    public void DeleteCustomAction(string name)
    {
        var filePath = FindSingleFile("customActions.json");
        if (!File.Exists(filePath)) return;

        var json = File.ReadAllText(filePath);
        var root = JsonNode.Parse(json, NodeOptions, DocOptions)?.AsObject();
        if (root == null) return;

        if (root.Remove(name))
        {
            WriteJsonFile(filePath, root);
        }
    }

    #endregion

    // ═══════════════════════════════════════════════════════════════
    // Program Units
    // ═══════════════════════════════════════════════════════════════

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

    public void SaveProgramUnitGroup(ProgramUnitGroup group)
    {
        var filePath = FindSingleFile("programUnits.json");
        var root = LoadOrCreateJsonObject(filePath);

        var groupsArray = root["programUnitGroups"]?.AsArray() ?? new JsonArray();

        if (group.Id == Guid.Empty) group.Id = Guid.NewGuid();

        var idStr = group.Id.ToString();
        var existingIndex = FindIndexById(groupsArray, idStr);
        var node = BuildProgramUnitGroupNode(group);

        // Preserve existing programUnits inside the group
        if (existingIndex >= 0)
        {
            var existingUnits = groupsArray[existingIndex]?["programUnits"];
            if (existingUnits != null)
            {
                var cloned = JsonNode.Parse(existingUnits.ToJsonString());
                node["programUnits"] = cloned;
            }
            groupsArray[existingIndex] = node;
        }
        else
        {
            node["programUnits"] = new JsonArray();
            groupsArray.Add(node);
        }

        root["programUnitGroups"] = groupsArray;
        WriteJsonFile(filePath, root);
    }

    public void DeleteProgramUnitGroup(string id)
    {
        var filePath = FindSingleFile("programUnits.json");
        if (!File.Exists(filePath)) return;

        var json = File.ReadAllText(filePath);
        var root = JsonNode.Parse(json, NodeOptions, DocOptions)?.AsObject();
        var groupsArray = root?["programUnitGroups"]?.AsArray();
        if (groupsArray == null) return;

        // Parse the GUID from the id (may be prefixed)
        var guidStr = ExtractGuid(id);
        var index = FindIndexById(groupsArray, guidStr);
        if (index >= 0)
        {
            groupsArray.RemoveAt(index);
            WriteJsonFile(filePath, root!);
        }
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

    public void SaveProgramUnit(ProgramUnit unit)
    {
        var filePath = FindSingleFile("programUnits.json");
        var root = LoadOrCreateJsonObject(filePath);
        var groupsArray = root["programUnitGroups"]?.AsArray() ?? new JsonArray();

        if (unit.Id == Guid.Empty) unit.Id = Guid.NewGuid();

        var groupIdStr = unit.GroupId?.ToString();
        if (groupIdStr == null)
            throw new InvalidOperationException("ProgramUnit.GroupId is required");

        // Find the parent group
        JsonObject? parentGroup = null;
        for (int i = 0; i < groupsArray.Count; i++)
        {
            var g = groupsArray[i]?.AsObject();
            if (g?["id"]?.GetValue<string>() == groupIdStr)
            {
                parentGroup = g;
                break;
            }
        }

        if (parentGroup == null)
            throw new InvalidOperationException($"Parent group '{groupIdStr}' not found");

        var unitsArray = parentGroup["programUnits"]?.AsArray() ?? new JsonArray();

        // Remove from any other group first (in case GroupId changed)
        RemoveProgramUnitFromAllGroups(groupsArray, unit.Id.ToString(), groupIdStr);

        var idStr = unit.Id.ToString();
        var existingIndex = FindIndexById(unitsArray, idStr);
        var node = BuildProgramUnitNode(unit);

        if (existingIndex >= 0)
        {
            unitsArray[existingIndex] = node;
        }
        else
        {
            unitsArray.Add(node);
        }

        parentGroup["programUnits"] = unitsArray;
        root["programUnitGroups"] = groupsArray;
        WriteJsonFile(filePath, root);
    }

    public void DeleteProgramUnit(string id)
    {
        var filePath = FindSingleFile("programUnits.json");
        if (!File.Exists(filePath)) return;

        var json = File.ReadAllText(filePath);
        var root = JsonNode.Parse(json, NodeOptions, DocOptions)?.AsObject();
        var groupsArray = root?["programUnitGroups"]?.AsArray();
        if (groupsArray == null) return;

        var guidStr = ExtractGuid(id);
        if (RemoveProgramUnitFromAllGroups(groupsArray, guidStr, null))
        {
            WriteJsonFile(filePath, root!);
        }
    }

    private static bool RemoveProgramUnitFromAllGroups(JsonArray groupsArray, string unitId, string? excludeGroupId)
    {
        bool removed = false;
        for (int i = 0; i < groupsArray.Count; i++)
        {
            var g = groupsArray[i]?.AsObject();
            if (g == null) continue;
            if (excludeGroupId != null && g["id"]?.GetValue<string>() == excludeGroupId) continue;

            var units = g["programUnits"]?.AsArray();
            if (units == null) continue;

            var idx = FindIndexById(units, unitId);
            if (idx >= 0)
            {
                units.RemoveAt(idx);
                removed = true;
            }
        }
        return removed;
    }

    #endregion

    // ═══════════════════════════════════════════════════════════════
    // Security
    // ═══════════════════════════════════════════════════════════════

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

                JsonElement commentsElement = default;
                root.TryGetProperty("groupComments", out commentsElement);

                foreach (var prop in groupsElement.EnumerateObject())
                {
                    var group = new SecurityGroupDefinition
                    {
                        Id = $"SecurityGroupDefs/{prop.Name}",
                        Name = prop.Value.ValueKind == JsonValueKind.Object
                            ? JsonSerializer.Deserialize<TranslatedString>(prop.Value.GetRawText(), ReadOptions)
                            : null,
                    };

                    if (commentsElement.ValueKind == JsonValueKind.Object &&
                        commentsElement.TryGetProperty(prop.Name, out var comment) &&
                        comment.ValueKind == JsonValueKind.Object)
                    {
                        group.Comment = JsonSerializer.Deserialize<TranslatedString>(comment.GetRawText(), ReadOptions);
                    }

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

    public void SaveSecurityGroup(SecurityGroupDefinition group)
    {
        var filePath = FindSingleFile("security.json");
        var root = LoadOrCreateJsonObject(filePath);

        var groups = root["groups"]?.AsObject() ?? new JsonObject();
        var comments = root["groupComments"]?.AsObject() ?? new JsonObject();

        // Extract GUID from compound ID
        var guid = ExtractGuid(group.Id ?? Guid.NewGuid().ToString());

        if (group.Name != null)
            groups[guid] = SerializeTranslatedString(group.Name);

        if (group.Comment != null)
            comments[guid] = SerializeTranslatedString(group.Comment);
        else
            comments.Remove(guid);

        root["groups"] = groups;
        if (comments.Count > 0)
            root["groupComments"] = comments;
        WriteJsonFile(filePath, root);
    }

    public void DeleteSecurityGroup(string id)
    {
        var filePath = FindSingleFile("security.json");
        if (!File.Exists(filePath)) return;

        var json = File.ReadAllText(filePath);
        var root = JsonNode.Parse(json, NodeOptions, DocOptions)?.AsObject();
        if (root == null) return;

        var guid = ExtractGuid(id);
        var changed = false;

        if (root["groups"]?.AsObject() is { } groups && groups.Remove(guid))
            changed = true;
        if (root["groupComments"]?.AsObject() is { } comments && comments.Remove(guid))
            changed = true;

        // Remove related rights
        if (root["rights"]?.AsArray() is { } rights)
        {
            for (int i = rights.Count - 1; i >= 0; i--)
            {
                if (rights[i]?["groupId"]?.GetValue<string>() == guid)
                {
                    rights.RemoveAt(i);
                    changed = true;
                }
            }
        }

        if (changed)
            WriteJsonFile(filePath, root);
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

    public void SaveSecurityRight(Right right)
    {
        var filePath = FindSingleFile("security.json");
        var root = LoadOrCreateJsonObject(filePath);

        var rightsArray = root["rights"]?.AsArray() ?? new JsonArray();

        // Generate ID if missing
        var rawId = ExtractGuid(right.Id ?? Guid.NewGuid().ToString());

        var existingIndex = -1;
        for (int i = 0; i < rightsArray.Count; i++)
        {
            if (rightsArray[i]?["id"]?.GetValue<string>() == rawId)
            {
                existingIndex = i;
                break;
            }
        }

        var node = BuildSecurityRightNode(rawId, right);

        if (existingIndex >= 0)
        {
            rightsArray[existingIndex] = node;
        }
        else
        {
            rightsArray.Add(node);
        }

        root["rights"] = rightsArray;

        // Ensure groups object exists
        if (root["groups"] == null)
            root["groups"] = new JsonObject();

        WriteJsonFile(filePath, root);
    }

    public void DeleteSecurityRight(string id)
    {
        var filePath = FindSingleFile("security.json");
        if (!File.Exists(filePath)) return;

        var json = File.ReadAllText(filePath);
        var root = JsonNode.Parse(json, NodeOptions, DocOptions)?.AsObject();
        var rightsArray = root?["rights"]?.AsArray();
        if (rightsArray == null) return;

        var rawId = ExtractGuid(id);
        for (int i = rightsArray.Count - 1; i >= 0; i--)
        {
            if (rightsArray[i]?["id"]?.GetValue<string>() == rawId)
            {
                rightsArray.RemoveAt(i);
                WriteJsonFile(filePath, root!);
                return;
            }
        }
    }

    #endregion

    // ═══════════════════════════════════════════════════════════════
    // Culture
    // ═══════════════════════════════════════════════════════════════

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
                            ? JsonSerializer.Deserialize<TranslatedString>(prop.Value.GetRawText(), ReadOptions)
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

    public void SaveLanguage(LanguageDefinition language)
    {
        var filePath = FindSingleFile("culture.json");
        var root = LoadOrCreateJsonObject(filePath);

        var languages = root["languages"]?.AsObject() ?? new JsonObject();

        var code = language.Culture;
        if (string.IsNullOrEmpty(code))
            throw new InvalidOperationException("Language culture code is required");

        if (language.Name != null)
            languages[code] = SerializeTranslatedString(language.Name);

        root["languages"] = languages;

        // Set defaultLanguage if not present
        if (root["defaultLanguage"] == null)
            root["defaultLanguage"] = code;

        WriteJsonFile(filePath, root);
    }

    public void DeleteLanguage(string id)
    {
        var filePath = FindSingleFile("culture.json");
        if (!File.Exists(filePath)) return;

        var json = File.ReadAllText(filePath);
        var root = JsonNode.Parse(json, NodeOptions, DocOptions)?.AsObject();
        if (root == null) return;

        var code = ExtractSuffix(id, "LanguageDefs/");

        var languages = root["languages"]?.AsObject();
        if (languages == null || !languages.Remove(code)) return;

        // Update defaultLanguage if it was the deleted one
        if (root["defaultLanguage"]?.GetValue<string>() == code)
        {
            var firstRemaining = languages.FirstOrDefault();
            root["defaultLanguage"] = firstRemaining.Key ?? "en";
        }

        WriteJsonFile(filePath, root);
    }

    #endregion

    // ═══════════════════════════════════════════════════════════════
    // Translations
    // ═══════════════════════════════════════════════════════════════

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
                            ? JsonSerializer.Deserialize<TranslatedString>(prop.Value.GetRawText(), ReadOptions)
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

    public void SaveTranslation(TranslationEntry translation)
    {
        var filePath = FindSingleFile("translations.json");
        var root = LoadOrCreateJsonObject(filePath);

        var key = translation.Key;
        if (string.IsNullOrEmpty(key))
            throw new InvalidOperationException("Translation key is required");

        if (translation.Values != null)
            root[key] = SerializeTranslatedString(translation.Values);

        WriteJsonFile(filePath, root);
    }

    public void DeleteTranslation(string id)
    {
        var filePath = FindSingleFile("translations.json");
        if (!File.Exists(filePath)) return;

        var json = File.ReadAllText(filePath);
        var root = JsonNode.Parse(json, NodeOptions, DocOptions)?.AsObject();
        if (root == null) return;

        var key = ExtractSuffix(id, "TranslationDefs/");
        if (root.Remove(key))
        {
            WriteJsonFile(filePath, root);
        }
    }

    #endregion

    // ═══════════════════════════════════════════════════════════════
    // File Watching
    // ═══════════════════════════════════════════════════════════════

    #region File Watching

    public void StartWatching()
    {
        foreach (var targetPath in TargetPaths)
        {
            // Watch Model/ directory for PO/Attribute/Query changes
            var modelDir = Path.Combine(targetPath, "Model");
            if (Directory.Exists(modelDir))
            {
                var modelWatcher = new FileSystemWatcher(modelDir, "*.json")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                    EnableRaisingEvents = true
                };
                modelWatcher.Changed += OnFileChanged;
                modelWatcher.Created += OnFileChanged;
                modelWatcher.Deleted += OnFileChanged;
                modelWatcher.Renamed += OnFileRenamed;
                _watchers.Add(modelWatcher);
            }

            // Watch root App_Data/ for single-object files
            if (Directory.Exists(targetPath))
            {
                var rootWatcher = new FileSystemWatcher(targetPath, "*.json")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true
                };
                rootWatcher.Changed += OnFileChanged;
                rootWatcher.Created += OnFileChanged;
                rootWatcher.Deleted += OnFileChanged;
                _watchers.Add(rootWatcher);
            }
        }
    }

    public void StopWatching()
    {
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();

        foreach (var kvp in _debounceTokens)
        {
            kvp.Value.Cancel();
            kvp.Value.Dispose();
        }
        _debounceTokens.Clear();
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (_isWriting) return;
        DebouncedNotify(e.FullPath, e.ChangeType);
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        if (_isWriting) return;
        DebouncedNotify(e.FullPath, WatcherChangeTypes.Renamed);
    }

    private void DebouncedNotify(string fullPath, WatcherChangeTypes changeType)
    {
        if (_debounceTokens.TryGetValue(fullPath, out var existingCts))
        {
            existingCts.Cancel();
            existingCts.Dispose();
        }

        var cts = new CancellationTokenSource();
        _debounceTokens[fullPath] = cts;

        Task.Delay(300, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
            {
                _debounceTokens.TryRemove(fullPath, out _);
                FileChanged?.Invoke(this, new FileChangedEventArgs
                {
                    FilePath = fullPath,
                    ChangeType = changeType
                });
            }
        }, TaskScheduler.Default);
    }

    #endregion

    // ═══════════════════════════════════════════════════════════════
    // JSON Node Builder Helpers (stable property ordering)
    // ═══════════════════════════════════════════════════════════════

    #region Node Builders

    private static JsonObject BuildPersistentObjectNode(EntityTypeDefinition po)
    {
        var node = new JsonObject();
        node.Add("id", po.Id.ToString());
        node.Add("name", po.Name);
        if (po.Description != null) node.Add("description", SerializeTranslatedString(po.Description));
        if (po.ClrType != null) node.Add("clrType", po.ClrType);
        if (po.QueryType != null) node.Add("queryType", po.QueryType);
        if (po.IndexName != null) node.Add("indexName", po.IndexName);
        if (po.Alias != null) node.Add("alias", po.Alias);
        if (po.DisplayFormat != null) node.Add("displayFormat", po.DisplayFormat);
        if (po.DisplayAttribute != null) node.Add("displayAttribute", po.DisplayAttribute);
        if (po.IsReadOnly) node.Add("isReadOnly", true);
        if (po.IsHidden) node.Add("isHidden", true);
        node.Add("tabs", new JsonArray());
        node.Add("groups", new JsonArray());
        node.Add("attributes", new JsonArray());
        return node;
    }

    private static JsonObject BuildAttributeNode(EntityAttributeDefinition attr)
    {
        var node = new JsonObject();
        node.Add("id", attr.Id.ToString());
        node.Add("name", attr.Name);
        if (attr.Label != null) node.Add("label", SerializeTranslatedString(attr.Label));
        node.Add("dataType", attr.DataType);
        node.Add("isRequired", attr.IsRequired);
        node.Add("isVisible", attr.IsVisible);
        node.Add("isReadOnly", attr.IsReadOnly);
        node.Add("order", attr.Order);
        node.Add("isArray", attr.IsArray);
        if (attr.InCollectionType.HasValue) node.Add("inCollectionType", attr.InCollectionType.Value);
        if (attr.InQueryType.HasValue) node.Add("inQueryType", attr.InQueryType.Value);
        if (attr.Query != null) node.Add("query", attr.Query);
        if (attr.ReferenceType != null) node.Add("referenceType", attr.ReferenceType);
        if (attr.AsDetailType != null) node.Add("asDetailType", attr.AsDetailType);
        if (attr.LookupReferenceType != null) node.Add("lookupReferenceType", attr.LookupReferenceType);
        if (attr.EditMode != null) node.Add("editMode", attr.EditMode);
        node.Add("showedOn", attr.ShowedOn.ToString());
        node.Add("rules", SerializeRules(attr.Rules));
        if (attr.Group.HasValue) node.Add("group", attr.Group.Value.ToString());
        if (attr.ColumnSpan.HasValue) node.Add("columnSpan", attr.ColumnSpan.Value);
        if (attr.Renderer != null) node.Add("renderer", attr.Renderer);
        if (attr.RendererOptions != null)
        {
            node.Add("rendererOptions", JsonSerializer.SerializeToNode(attr.RendererOptions, WriteOptions));
        }
        return node;
    }

    private static JsonObject BuildQueryNode(SparkQuery query)
    {
        var node = new JsonObject();
        node.Add("id", query.Id.ToString());
        node.Add("name", query.Name);
        if (query.Description != null) node.Add("description", SerializeTranslatedString(query.Description));
        if (query.Source != null) node.Add("source", query.Source);
        if (query.Alias != null) node.Add("alias", query.Alias);
        if (query.SortColumns is { Length: > 0 })
        {
            var arr = new JsonArray();
            foreach (var sc in query.SortColumns)
            {
                var scNode = new JsonObject();
                scNode.Add("property", sc.Property);
                scNode.Add("direction", sc.Direction);
                arr.Add(scNode);
            }
            node.Add("sortColumns", arr);
        }
        if (query.RenderMode != SparkQueryRenderMode.Pagination)
            node.Add("renderMode", query.RenderMode.ToString());
        if (query.IndexName != null) node.Add("indexName", query.IndexName);
        if (query.UseProjection) node.Add("useProjection", true);
        if (query.EntityType != null) node.Add("entityType", query.EntityType);
        if (query.IsStreamingQuery) node.Add("isStreamingQuery", true);
        return node;
    }

    private static JsonObject BuildCustomActionNode(CustomActionDefinition action)
    {
        var node = new JsonObject();
        if (action.DisplayName != null) node.Add("displayName", SerializeTranslatedString(action.DisplayName));
        if (action.Icon != null) node.Add("icon", action.Icon);
        if (action.Description != null) node.Add("description", action.Description);
        node.Add("showedOn", action.ShowedOn);
        if (action.SelectionRule != null) node.Add("selectionRule", action.SelectionRule);
        node.Add("refreshOnCompleted", action.RefreshOnCompleted);
        if (action.ConfirmationMessageKey != null) node.Add("confirmationMessageKey", action.ConfirmationMessageKey);
        if (action.Offset != 0) node.Add("offset", action.Offset);
        return node;
    }

    private static JsonObject BuildProgramUnitGroupNode(ProgramUnitGroup group)
    {
        var node = new JsonObject();
        node.Add("id", group.Id.ToString());
        if (group.Name != null) node.Add("name", SerializeTranslatedString(group.Name));
        if (group.Icon != null) node.Add("icon", group.Icon);
        node.Add("order", group.Order);
        return node;
    }

    private static JsonObject BuildProgramUnitNode(ProgramUnit unit)
    {
        var node = new JsonObject();
        node.Add("id", unit.Id.ToString());
        if (unit.Name != null) node.Add("name", SerializeTranslatedString(unit.Name));
        if (unit.Icon != null) node.Add("icon", unit.Icon);
        node.Add("type", unit.Type);
        if (unit.QueryId.HasValue) node.Add("queryId", unit.QueryId.Value.ToString());
        if (unit.PersistentObjectId.HasValue) node.Add("persistentObjectId", unit.PersistentObjectId.Value.ToString());
        node.Add("order", unit.Order);
        if (unit.Alias != null) node.Add("alias", unit.Alias);
        return node;
    }

    private static JsonObject BuildSecurityRightNode(string rawId, Right right)
    {
        var node = new JsonObject();
        node.Add("id", rawId);
        node.Add("resource", right.Resource);
        if (right.GroupId != null) node.Add("groupId", right.GroupId);
        if (right.IsDenied) node.Add("isDenied", true);
        if (right.IsImportant) node.Add("isImportant", true);
        return node;
    }

    #endregion

    // ═══════════════════════════════════════════════════════════════
    // Sorting Helpers
    // ═══════════════════════════════════════════════════════════════

    #region Sorting

    // No array/object sorting is applied. Existing items preserve their
    // position (keyed by ID for arrays, by property name for objects).
    // New items are appended at the end. This produces minimal git diffs
    // because changing an attribute's "order" field only modifies that
    // one value — it does not reshuffle the array.

    #endregion

    // ═══════════════════════════════════════════════════════════════
    // Serialization Helpers
    // ═══════════════════════════════════════════════════════════════

    #region Serialization

    private static JsonObject SerializeTranslatedString(TranslatedString ts)
    {
        var node = new JsonObject();
        foreach (var kvp in ts.Translations.OrderBy(k => k.Key, StringComparer.Ordinal))
            node.Add(kvp.Key, kvp.Value);
        return node;
    }

    private static JsonArray SerializeRules(ValidationRule[]? rules)
    {
        var arr = new JsonArray();
        if (rules == null) return arr;

        foreach (var rule in rules)
        {
            var rNode = new JsonObject();
            rNode.Add("type", rule.Type);
            if (rule.Value != null)
            {
                rNode.Add("value", JsonSerializer.SerializeToNode(rule.Value, WriteOptions));
            }
            if (rule.Min.HasValue) rNode.Add("min", rule.Min.Value);
            if (rule.Max.HasValue) rNode.Add("max", rule.Max.Value);
            if (rule.Message != null) rNode.Add("message", SerializeTranslatedString(rule.Message));
            arr.Add(rNode);
        }

        return arr;
    }

    #endregion

    // ═══════════════════════════════════════════════════════════════
    // File I/O Helpers
    // ═══════════════════════════════════════════════════════════════

    #region File I/O

    private void WriteJsonFile(string path, JsonNode root)
    {
        var json = root.ToJsonString(WriteOptions);
        if (!json.EndsWith('\n')) json += "\n";
        AtomicWriteFileWithSuppression(path, json);
    }

    private void AtomicWriteFileWithSuppression(string path, string content)
    {
        var semaphore = GetLock(path);
        semaphore.Wait();
        try
        {
            _isWriting = true;
            var tempPath = path + ".tmp";
            File.WriteAllText(tempPath, content);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            // Delay re-enabling to let FSW events settle
            Task.Delay(500).ContinueWith(_ =>
            {
                _isWriting = false;
                semaphore.Release();
            }, TaskScheduler.Default);
        }
    }

    private void DeleteFileWithSuppression(string path)
    {
        var semaphore = GetLock(path);
        semaphore.Wait();
        try
        {
            _isWriting = true;
            if (File.Exists(path))
                File.Delete(path);
        }
        finally
        {
            Task.Delay(500).ContinueWith(_ =>
            {
                _isWriting = false;
                semaphore.Release();
            }, TaskScheduler.Default);
        }
    }

    private SemaphoreSlim GetLock(string filePath)
        => _fileLocks.GetOrAdd(Path.GetFullPath(filePath), _ => new SemaphoreSlim(1, 1));

    private string FindSingleFile(string fileName)
    {
        foreach (var targetPath in TargetPaths)
        {
            var filePath = Path.Combine(targetPath, fileName);
            if (File.Exists(filePath)) return filePath;
        }
        // Default to first target path for new files
        return Path.Combine(TargetPaths[0], fileName);
    }

    private string? FindModelFileForPO(string poName)
    {
        foreach (var targetPath in TargetPaths)
        {
            var modelDir = Path.Combine(targetPath, "Model");
            if (!Directory.Exists(modelDir)) continue;

            // Try direct name match first
            var filePath = Path.Combine(modelDir, $"{poName}.json");
            if (File.Exists(filePath)) return filePath;

            // Fall back to scanning files for matching PO name
            foreach (var file in Directory.GetFiles(modelDir, "*.json"))
            {
                var po = ParsePersistentObject(file);
                if (po != null && string.Equals(po.Name, poName, StringComparison.OrdinalIgnoreCase))
                    return file;
            }
        }
        return null;
    }

    private JsonObject LoadOrCreateJsonObject(string filePath)
    {
        if (File.Exists(filePath))
        {
            var json = File.ReadAllText(filePath);
            return JsonNode.Parse(json, NodeOptions, DocOptions)?.AsObject() ?? new JsonObject();
        }
        return new JsonObject();
    }

    #endregion

    // ═══════════════════════════════════════════════════════════════
    // JSON Read Helpers
    // ═══════════════════════════════════════════════════════════════

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

    private static TranslatedString? DeserializeTranslatedString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Object)
        {
            return JsonSerializer.Deserialize<TranslatedString>(prop.GetRawText(), ReadOptions);
        }
        return null;
    }

    private static int FindIndexById(JsonArray array, string id)
    {
        for (int i = 0; i < array.Count; i++)
        {
            var obj = array[i]?.AsObject();
            if (obj?["id"]?.GetValue<string>() == id)
                return i;
        }
        return -1;
    }

    private static string ExtractGuid(string compoundId)
    {
        // Strip prefixes like "SecurityGroupDefs/", "SecurityRightDefs/", etc.
        var slashIndex = compoundId.LastIndexOf('/');
        return slashIndex >= 0 ? compoundId[(slashIndex + 1)..] : compoundId;
    }

    private static string ExtractSuffix(string id, string prefix)
    {
        return id.StartsWith(prefix, StringComparison.Ordinal) ? id[prefix.Length..] : id;
    }

    #endregion

    // ═══════════════════════════════════════════════════════════════
    // Dispose
    // ═══════════════════════════════════════════════════════════════

    public void Dispose()
    {
        StopWatching();
        foreach (var kvp in _fileLocks)
            kvp.Value.Dispose();
        _fileLocks.Clear();
    }
}
