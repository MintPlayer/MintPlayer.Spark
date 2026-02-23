using System.Reflection;
using System.Text.Json;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Services;

public interface ILookupReferenceService
{
    Task<IEnumerable<LookupReferenceListItem>> GetAllAsync();
    Task<LookupReferenceDto?> GetAsync(string name);
    Task<LookupReferenceValueDto> AddValueAsync(string name, LookupReferenceValueDto value);
    Task<LookupReferenceValueDto> UpdateValueAsync(string name, string key, LookupReferenceValueDto value);
    Task DeleteValueAsync(string name, string key);
}

public class LookupReferenceListItem
{
    public required string Name { get; set; }
    public required bool IsTransient { get; set; }
    public int ValueCount { get; set; }
    public ELookupDisplayType DisplayType { get; set; } = ELookupDisplayType.Dropdown;
}

public class LookupReferenceDto
{
    public required string Name { get; set; }
    public required bool IsTransient { get; set; }
    public ELookupDisplayType DisplayType { get; set; } = ELookupDisplayType.Dropdown;
    public List<LookupReferenceValueDto> Values { get; set; } = new();
}

public class LookupReferenceValueDto
{
    public required string Key { get; set; }
    public required TranslatedString Values { get; set; }
    public bool IsActive { get; set; } = true;
    public Dictionary<string, object>? Extra { get; set; }
}

[Register(typeof(ILookupReferenceService), ServiceLifetime.Scoped)]
internal partial class LookupReferenceService : ILookupReferenceService
{
    [Inject] private readonly ILookupReferenceDiscoveryService discoveryService;
    [Inject] private readonly IDocumentStore documentStore;

    public async Task<IEnumerable<LookupReferenceListItem>> GetAllAsync()
    {
        var result = new List<LookupReferenceListItem>();
        var lookupReferences = discoveryService.GetAllLookupReferences();

        foreach (var info in lookupReferences)
        {
            var item = new LookupReferenceListItem
            {
                Name = info.Name,
                IsTransient = info.IsTransient,
                ValueCount = 0,
                DisplayType = info.DisplayType
            };

            if (info.IsTransient)
            {
                // Get count from static Items property
                var items = GetTransientItems(info.Type);
                item.ValueCount = items?.Count ?? 0;
            }
            else
            {
                // Get count from database
                var dynamicLookup = await LoadDynamicLookupAsync(info.Name);
                item.ValueCount = dynamicLookup?.Values.Count ?? 0;
            }

            result.Add(item);
        }

        return result;
    }

    public async Task<LookupReferenceDto?> GetAsync(string name)
    {
        var info = discoveryService.GetLookupReference(name);
        if (info == null) return null;

        var dto = new LookupReferenceDto
        {
            Name = info.Name,
            IsTransient = info.IsTransient,
            DisplayType = info.DisplayType
        };

        if (info.IsTransient)
        {
            // Get values from static Items property
            var items = GetTransientItems(info.Type);
            if (items != null)
            {
                foreach (var item in items)
                {
                    var valueDto = TransientToDto(item);
                    if (valueDto != null)
                    {
                        dto.Values.Add(valueDto);
                    }
                }
            }
        }
        else
        {
            // Get values from database
            var dynamicLookup = await LoadDynamicLookupAsync(name);
            if (dynamicLookup != null)
            {
                dto.Values = dynamicLookup.Values.Select(v => v.ToDto()).ToList();
            }
        }

        return dto;
    }

    public async Task<LookupReferenceValueDto> AddValueAsync(string name, LookupReferenceValueDto value)
    {
        var info = discoveryService.GetLookupReference(name);
        if (info == null)
            throw new InvalidOperationException($"LookupReference '{name}' not found");

        if (info.IsTransient)
            throw new InvalidOperationException($"Cannot add values to transient LookupReference '{name}'");

        using var session = documentStore.OpenAsyncSession();

        var documentId = $"LookupReferences/{name}";
        var document = await session.LoadAsync<DynamicLookupReferenceDocument>(documentId);

        if (document == null)
        {
            document = new DynamicLookupReferenceDocument
            {
                Id = documentId,
                Name = name,
                Values = new List<DynamicLookupReferenceValue>()
            };
        }

        // Check for duplicate key
        if (document.Values.Any(v => v.Key == value.Key))
            throw new InvalidOperationException($"A value with key '{value.Key}' already exists");

        document.Values.Add(DynamicLookupReferenceValue.FromDto(value));
        await session.StoreAsync(document);
        await session.SaveChangesAsync();

        return value;
    }

    public async Task<LookupReferenceValueDto> UpdateValueAsync(string name, string key, LookupReferenceValueDto value)
    {
        var info = discoveryService.GetLookupReference(name);
        if (info == null)
            throw new InvalidOperationException($"LookupReference '{name}' not found");

        if (info.IsTransient)
            throw new InvalidOperationException($"Cannot update values in transient LookupReference '{name}'");

        using var session = documentStore.OpenAsyncSession();

        var documentId = $"LookupReferences/{name}";
        var document = await session.LoadAsync<DynamicLookupReferenceDocument>(documentId);

        if (document == null)
            throw new InvalidOperationException($"LookupReference '{name}' has no values in database");

        var existingValue = document.Values.FirstOrDefault(v => v.Key == key);
        if (existingValue == null)
            throw new InvalidOperationException($"Value with key '{key}' not found");

        // Update the value
        existingValue.Translations = value.Values.Translations;
        existingValue.IsActive = value.IsActive;
        existingValue.Extra = value.Extra;

        // If key changed, update it
        if (value.Key != key)
        {
            if (document.Values.Any(v => v.Key == value.Key && v != existingValue))
                throw new InvalidOperationException($"A value with key '{value.Key}' already exists");
            existingValue.Key = value.Key;
        }

        await session.SaveChangesAsync();

        return existingValue.ToDto();
    }

    public async Task DeleteValueAsync(string name, string key)
    {
        var info = discoveryService.GetLookupReference(name);
        if (info == null)
            throw new InvalidOperationException($"LookupReference '{name}' not found");

        if (info.IsTransient)
            throw new InvalidOperationException($"Cannot delete values from transient LookupReference '{name}'");

        using var session = documentStore.OpenAsyncSession();

        var documentId = $"LookupReferences/{name}";
        var document = await session.LoadAsync<DynamicLookupReferenceDocument>(documentId);

        if (document == null)
            throw new InvalidOperationException($"LookupReference '{name}' has no values in database");

        var valueToRemove = document.Values.FirstOrDefault(v => v.Key == key);
        if (valueToRemove == null)
            throw new InvalidOperationException($"Value with key '{key}' not found");

        document.Values.Remove(valueToRemove);
        await session.SaveChangesAsync();
    }

    private async Task<DynamicLookupReferenceDocument?> LoadDynamicLookupAsync(string name)
    {
        using var session = documentStore.OpenAsyncSession();
        var documentId = $"LookupReferences/{name}";
        return await session.LoadAsync<DynamicLookupReferenceDocument>(documentId);
    }

    private System.Collections.IList? GetTransientItems(Type transientType)
    {
        // Look for static Items property
        var itemsProperty = transientType.GetProperty("Items", BindingFlags.Public | BindingFlags.Static);
        if (itemsProperty == null) return null;

        var value = itemsProperty.GetValue(null);
        if (value is System.Collections.IList list)
            return list;

        // If it's IReadOnlyCollection, convert to list
        if (value is System.Collections.IEnumerable enumerable)
            return enumerable.Cast<object>().ToList();

        return null;
    }

    private LookupReferenceValueDto? TransientToDto(object transientItem)
    {
        if (transientItem is not TransientLookupReference item)
            return null;

        // Get the Key value via reflection (could be string, enum, or other TKey type)
        var keyProp = transientItem.GetType().GetProperty("Key");
        var keyValue = keyProp?.GetValue(transientItem);
        var key = keyValue?.ToString() ?? string.Empty;

        // Get extra properties (properties beyond Key, Description, Values, DisplayType)
        var extraProps = new Dictionary<string, object>();
        var itemType = transientItem.GetType();
        var baseProps = new HashSet<string> { "Key", "Description", "Values", "DisplayType" };

        foreach (var prop in itemType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!baseProps.Contains(prop.Name))
            {
                var value = prop.GetValue(transientItem);
                if (value != null)
                {
                    extraProps[ToCamelCase(prop.Name)] = value;
                }
            }
        }

        return new LookupReferenceValueDto
        {
            Key = key,
            Values = item.Values,
            IsActive = true,
            Extra = extraProps.Count > 0 ? extraProps : null
        };
    }

    private static string ToCamelCase(string str)
    {
        if (string.IsNullOrEmpty(str)) return str;
        return char.ToLowerInvariant(str[0]) + str[1..];
    }
}

/// <summary>
/// RavenDB document for storing dynamic lookup reference values.
/// Uses its own value type to match the stored format in RavenDB (Newtonsoft.Json).
/// </summary>
internal class DynamicLookupReferenceDocument
{
    public string? Id { get; set; }
    public required string Name { get; set; }
    public List<DynamicLookupReferenceValue> Values { get; set; } = new();
}

internal class DynamicLookupReferenceValue
{
    public required string Key { get; set; }
    public Dictionary<string, string> Translations { get; set; } = new();
    public bool IsActive { get; set; } = true;
    public Dictionary<string, object>? Extra { get; set; }

    public LookupReferenceValueDto ToDto()
    {
        return new LookupReferenceValueDto
        {
            Key = Key,
            Values = new TranslatedString { Translations = Translations },
            IsActive = IsActive,
            Extra = Extra
        };
    }

    public static DynamicLookupReferenceValue FromDto(LookupReferenceValueDto dto)
    {
        return new DynamicLookupReferenceValue
        {
            Key = dto.Key,
            Translations = dto.Values.Translations,
            IsActive = dto.IsActive,
            Extra = dto.Extra
        };
    }
}
