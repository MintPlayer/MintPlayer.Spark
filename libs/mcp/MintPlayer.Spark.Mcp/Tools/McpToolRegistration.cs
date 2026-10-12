using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MintPlayer.Spark.Mcp.Tools;

/// <summary>
/// Registers tool classes under the prefix (MCP PRD D21): the generic tools and the application's own, which
/// are written unprefixed. The SDK's <c>WithTools&lt;T&gt;()</c>, with the name rewritten.
/// </summary>
internal static class McpToolRegistration
{
    public static void Register(IServiceCollection services, string prefix, IEnumerable<Type> toolTypes)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in toolTypes)
        {
            var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null);

            foreach (var method in methods)
            {
                // Built once here, without services, only to learn the SDK's own name for the tool and catch a
                // duplicate at startup rather than as a silently shadowed tool.
                var name = Prefixed(prefix, Create(type, method, services: null, name: null).ProtocolTool.Name);
                if (!names.Add(name))
                    throw new InvalidOperationException($"Two MCP tools are named '{name}' ({type.FullName}.{method.Name}). Tool names must be unique, the generic ones (discover, describe, query, get) included.");

                // A tool that runs an action writes: listed as read-only, it would run for agents allowed to read only.
                if (method.GetCustomAttribute<McpServerToolAttribute>()!.ReadOnly && method.GetCustomAttributes<McpRunsActionAttribute>().Any())
                    throw new InvalidOperationException($"MCP tool '{name}' ({type.FullName}.{method.Name}) is ReadOnly but declares [McpRunsAction]. A tool that runs an action is a write: remove ReadOnly = true.");

                services.AddSingleton(sp => Create(type, method, sp, name));
            }
        }
    }

    /// <summary>The tool's permission (MCP PRD §4.5): <c>ReadOnly = true</c> needs Read; anything else, Write. Fails closed.</summary>
    public static Abstractions.EAgentPermission Needs(Tool tool)
        => tool.Annotations?.ReadOnlyHint == true ? Abstractions.EAgentPermission.Read : Abstractions.EAgentPermission.Write;

    internal static string Prefixed(string prefix, string name)
        => string.IsNullOrEmpty(prefix) ? name : prefix + "_" + name;

    private static McpServerTool Create(Type type, MethodInfo method, IServiceProvider? services, string? name)
    {
        var options = new McpServerToolCreateOptions { Services = services, Name = name };
        return method.IsStatic
            ? McpServerTool.Create(method, (object?)null, options)
            : McpServerTool.Create(
                method,
                request => ActivatorUtilities.CreateInstance(request.Services ?? throw new InvalidOperationException("An MCP tool call carries no services."), type),
                options);
    }
}
