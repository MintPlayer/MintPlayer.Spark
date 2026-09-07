using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Actions;
using System.Reflection;

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
                // Rethrow rather than return null. Null means "no such action" to every caller,
                // which turns a dependency the container could not satisfy into a 404 saying the
                // action does not exist -- pointing whoever is debugging at the action's name and
                // at customActions.json, neither of which is wrong. The real cause was log-only,
                // and an operator reading a 404 has no reason to go looking in the log at all.
                //
                // Surfacing it makes the failure a 500 that names the type and carries the
                // container's own message, which is what a misconfigured registration deserves.
                logger.LogError(ex, "Failed to resolve custom action '{ActionName}' (type: {Type})", actionName, type.FullName);

                throw new InvalidOperationException(
                    $"Custom action '{actionName}' is declared as {type.FullName} but could not be "
                    + "constructed. Its dependencies are most likely not registered -- check that "
                    + "the application calls AddCustomActions() and that every [Inject] dependency "
                    + "of the action has a registration.", ex);
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
