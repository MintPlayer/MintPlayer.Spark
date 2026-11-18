using System.Text.Json;

namespace MintPlayer.Spark.Cli;

/// <summary>Grants a new model type or custom action in <c>App_Data/security.json</c>, as the item templates' doc comments say.</summary>
internal static class SecurityRights
{
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Turns <c>--group</c> into a right's <c>groupId</c>: a well-known alias (<c>@authenticated</c>) or a group id stays
    /// as it is, and a group's name (case-insensitive) becomes its id from <c>groups</c>.
    /// </summary>
    public static string ResolveGroup(string securityJson, string group)
    {
        if (group.StartsWith('@') || Guid.TryParse(group, out _)) return group;

        using var document = JsonDocument.Parse(securityJson, ReadOptions);
        var names = new List<string>();
        if (document.RootElement.TryGetProperty("groups", out var groups) && groups.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in groups.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.String) continue;
                var name = entry.Value.GetString()!;
                if (string.Equals(name, group, StringComparison.OrdinalIgnoreCase)) return entry.Name;
                names.Add(name);
            }
        }
        throw new InvalidOperationException($"security.json has no group \"{group}\". Its groups: {string.Join(", ", names)}.");
    }

    /// <summary>Appends <c>{ "key", "resource", "groupId" }</c> to <c>rights</c>; a key that is taken is refused.</summary>
    public static string Add(string securityJson, string key, string resource, string groupId)
    {
        if (JsonText.ArrayContainsValue(securityJson, "rights", "key", key))
            throw new InvalidOperationException($"security.json already has a right with key \"{key}\".");

        var item = $$"""{ "key": {{JsonSerializer.Serialize(key)}}, "resource": {{JsonSerializer.Serialize(resource)}}, "groupId": {{JsonSerializer.Serialize(groupId)}} }""";
        return JsonText.AppendToArray(securityJson, "rights", item);
    }
}
