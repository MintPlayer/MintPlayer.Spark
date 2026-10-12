using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace MintPlayer.Spark.Mcp.Tools;

/// <summary>
/// Registers the application's prompt classes under the tool prefix (MCP PRD D21, §9: one prompt per adopter). The
/// SDK's <c>WithPrompts&lt;T&gt;()</c>, with the name rewritten exactly as <see cref="McpToolRegistration"/> rewrites
/// tool names, so a prompt and a tool of two Spark servers in one session never collide.
/// </summary>
/// <remarks>
/// A prompt is a message template the user picks in the client; it reads no data, so it needs no permission and is
/// listed to every caller. A prompt that would need data is a tool.
/// </remarks>
internal static class McpPromptRegistration
{
    public static void Register(IServiceCollection services, string prefix, IEnumerable<Type> promptTypes)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in promptTypes)
        {
            var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m => m.GetCustomAttribute<McpServerPromptAttribute>() is not null);

            foreach (var method in methods)
            {
                // Built once without services, only to learn the SDK's own name and catch a duplicate at startup.
                var name = McpToolRegistration.Prefixed(prefix, Create(type, method, services: null, name: null).ProtocolPrompt.Name);
                if (!names.Add(name))
                    throw new InvalidOperationException($"Two MCP prompts are named '{name}' ({type.FullName}.{method.Name}). Prompt names must be unique.");

                services.AddSingleton(sp => Create(type, method, sp, name));
            }
        }
    }

    private static McpServerPrompt Create(Type type, MethodInfo method, IServiceProvider? services, string? name)
    {
        var options = new McpServerPromptCreateOptions { Services = services, Name = name };
        return method.IsStatic
            ? McpServerPrompt.Create(method, (object?)null, options)
            : McpServerPrompt.Create(
                method,
                request => ActivatorUtilities.CreateInstance(request.Services ?? throw new InvalidOperationException("An MCP prompt request carries no services."), type),
                options);
    }
}
