using System.Reflection;
using Microsoft.Extensions.Logging;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Actions;

namespace MintPlayer.Spark.Services;

public interface ICustomActionResolver
{
    /// <summary>
    /// Resolves a custom action by name.
    /// Matches against class name with optional "Action" suffix.
    /// </summary>
    ICustomAction? Resolve(string actionName);

    /// <summary>
    /// Gets all registered custom action names.
    /// </summary>
    IReadOnlyList<string> GetRegisteredActionNames();
}

[Register(typeof(ICustomActionResolver), ServiceLifetime.Scoped)]
internal partial class CustomActionResolver : ICustomActionResolver
{
    [Inject] private readonly IServiceProvider serviceProvider;
    [Inject] private readonly ILogger<CustomActionResolver> logger;

    private static readonly Lazy<Dictionary<string, Type>> actionTypes = new(DiscoverActionTypes);

    public ICustomAction? Resolve(string actionName)
    {
        if (actionTypes.Value.TryGetValue(actionName, out var type))
        {
            try
            {
                var instance = serviceProvider.GetService(type)
                    ?? ActivatorUtilities.CreateInstance(serviceProvider, type);
                return instance as ICustomAction;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to resolve custom action '{ActionName}' (type: {Type})", actionName, type.FullName);
                return null;
            }
        }

        logger.LogWarning("Custom action '{ActionName}' not found", actionName);
        return null;
    }

    public IReadOnlyList<string> GetRegisteredActionNames()
    {
        return actionTypes.Value.Keys.ToList();
    }

    private static Dictionary<string, Type> DiscoverActionTypes()
    {
        var result = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                foreach (var type in assembly.GetTypes())
                {
                    if (type.IsAbstract || type.IsInterface)
                        continue;

                    if (!typeof(ICustomAction).IsAssignableFrom(type))
                        continue;

                    // Derive action name: strip optional "Action" suffix
                    var name = type.Name;
                    if (name.EndsWith("Action", StringComparison.Ordinal))
                        name = name[..^6];

                    result.TryAdd(name, type);
                }
            }
            catch (ReflectionTypeLoadException)
            {
                // Skip assemblies that can't be loaded
            }
        }

        return result;
    }
}
