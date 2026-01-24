using System.Reflection;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Services;

public interface ILookupReferenceDiscoveryService
{
    IReadOnlyCollection<LookupReferenceInfo> GetAllLookupReferences();
    LookupReferenceInfo? GetLookupReference(string name);
    bool IsTransient(string name);
    bool IsDynamic(string name);
}

public class LookupReferenceInfo
{
    public required string Name { get; init; }
    public required Type Type { get; init; }
    public required bool IsTransient { get; init; }
    public Type? ValueType { get; init; }  // For dynamic: the TValue type
    public ELookupDisplayType DisplayType { get; init; } = ELookupDisplayType.Dropdown;
}

[Register(typeof(ILookupReferenceDiscoveryService), ServiceLifetime.Singleton)]
internal partial class LookupReferenceDiscoveryService : ILookupReferenceDiscoveryService
{
    private readonly Dictionary<string, LookupReferenceInfo> _lookupReferences = new(StringComparer.OrdinalIgnoreCase);

    public LookupReferenceDiscoveryService()
    {
        // Discover lookup references from all loaded assemblies
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                DiscoverLookupReferences(assembly);
            }
            catch
            {
                // Ignore assemblies that can't be scanned
            }
        }
    }

    private void DiscoverLookupReferences(Assembly assembly)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch
        {
            return;
        }

        foreach (var type in types.Where(t => !t.IsAbstract && t.IsClass))
        {
            // Check for TransientLookupReference
            if (typeof(TransientLookupReference).IsAssignableFrom(type) && type != typeof(TransientLookupReference))
            {
                _lookupReferences[type.Name] = new LookupReferenceInfo
                {
                    Name = type.Name,
                    Type = type,
                    IsTransient = true,
                    DisplayType = GetDisplayType(type)
                };
            }
            // Check for DynamicLookupReference<T>
            else if (IsAssignableToGenericType(type, typeof(DynamicLookupReference<>)))
            {
                var valueType = GetGenericArgument(type, typeof(DynamicLookupReference<>));
                _lookupReferences[type.Name] = new LookupReferenceInfo
                {
                    Name = type.Name,
                    Type = type,
                    IsTransient = false,
                    ValueType = valueType,
                    DisplayType = GetDisplayType(type)
                };
            }
        }
    }

    private static ELookupDisplayType GetDisplayType(Type type)
    {
        // For TransientLookupReference: get DisplayType from one of the Items
        if (typeof(TransientLookupReference).IsAssignableFrom(type))
        {
            var itemsProp = type.GetProperty("Items", BindingFlags.Public | BindingFlags.Static);
            if (itemsProp != null)
            {
                var items = itemsProp.GetValue(null);
                if (items is System.Collections.IEnumerable enumerable)
                {
                    foreach (var item in enumerable)
                    {
                        if (item is TransientLookupReference transientItem)
                        {
                            return transientItem.DisplayType;
                        }
                    }
                }
            }
        }
        // For DynamicLookupReference: create a temporary instance to read the property
        else
        {
            try
            {
                var instance = Activator.CreateInstance(type);
                if (instance != null)
                {
                    var prop = type.GetProperty("DisplayType", BindingFlags.Public | BindingFlags.Instance);
                    if (prop != null)
                    {
                        var value = prop.GetValue(instance);
                        if (value is ELookupDisplayType displayType)
                            return displayType;
                    }
                }
            }
            catch
            {
                // If we can't create an instance, fall back to default
            }
        }

        return ELookupDisplayType.Dropdown;
    }

    public IReadOnlyCollection<LookupReferenceInfo> GetAllLookupReferences()
        => _lookupReferences.Values.ToList();

    public LookupReferenceInfo? GetLookupReference(string name)
        => _lookupReferences.TryGetValue(name, out var info) ? info : null;

    public bool IsTransient(string name)
        => _lookupReferences.TryGetValue(name, out var info) && info.IsTransient;

    public bool IsDynamic(string name)
        => _lookupReferences.TryGetValue(name, out var info) && !info.IsTransient;

    private static bool IsAssignableToGenericType(Type givenType, Type genericType)
    {
        var interfaceTypes = givenType.GetInterfaces();

        foreach (var it in interfaceTypes)
        {
            if (it.IsGenericType && it.GetGenericTypeDefinition() == genericType)
                return true;
        }

        if (givenType.IsGenericType && givenType.GetGenericTypeDefinition() == genericType)
            return true;

        var baseType = givenType.BaseType;
        if (baseType == null) return false;

        return IsAssignableToGenericType(baseType, genericType);
    }

    private static Type? GetGenericArgument(Type givenType, Type genericType)
    {
        // Check the type itself
        if (givenType.IsGenericType && givenType.GetGenericTypeDefinition() == genericType)
            return givenType.GetGenericArguments().FirstOrDefault();

        // Check base types
        var baseType = givenType.BaseType;
        while (baseType != null)
        {
            if (baseType.IsGenericType && baseType.GetGenericTypeDefinition() == genericType)
                return baseType.GetGenericArguments().FirstOrDefault();

            baseType = baseType.BaseType;
        }

        return null;
    }
}
