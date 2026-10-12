using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Mcp.Authentication;
using MintPlayer.Spark.Mcp.Http;
using MintPlayer.Spark.Mcp.Tools;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;

namespace MintPlayer.Spark.Mcp;

/// <summary>
/// The MCP server of a Spark application (MCP PRD D1, <c>docs/guide-mcp.md</c>):
/// <code>
/// builder.Services.AddSpark(builder.Configuration, spark =&gt;
/// {
///     spark.AddAuthentication&lt;SparkUser&gt;();
///     spark.AddMcp(mcp =&gt;
///     {
///         mcp.ServerName = "Contracts";
///         mcp.ToolPrefix = "contracts";          // contracts_query, ...; set it before you publish
///         mcp.Instructions = "Contracts, customers and invoices of the signed-in user.";
///         mcp.AgentPermissions = _ =&gt; EAgentPermission.Read;
///         mcp.AddTools&lt;ContractTools&gt;();
///     });
/// });
/// // App_Data/Model/Contract.json: "agentPermissions": "Read" — nothing is exposed until a type says so.
/// </code>
/// </summary>
public static class SparkMcpExtensions
{
    /// <summary>
    /// Registers the MCP server: the official SDK (<c>ModelContextProtocol.AspNetCore</c> 2.2.0, plain
    /// <c>Stateless</c> HTTP), the generic tools and <paramref name="configure"/>'s own, the bearer-only gate on
    /// the endpoint (cookies are ignored), the <c>Origin</c> check, call limits, audit events, and the agent layers
    /// inside <c>IPermissionService</c>; maps the endpoint at <see cref="SparkMcpOptions.Path"/> through
    /// <c>MapSpark()</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Called twice, or the options are unusable.</exception>
    public static ISparkBuilder AddMcp(this ISparkBuilder builder, Action<SparkMcpOptions>? configure = null)
    {
        var services = builder.Services;
        if (services.Any(d => d.ServiceType == typeof(SparkMcpOptions)))
            throw new InvalidOperationException("AddMcp was called twice. One application has one MCP server; configure it in one call.");

        var options = SparkMcpOptions.Create(builder.Configuration, configure);
        Validate(options);

        services.AddSingleton(options);
        services.AddSingleton<McpAuthentication>();
        services.AddSingleton<McpRateLimiter>();
        services.AddHttpContextAccessor();
        services.AddTransient<Microsoft.AspNetCore.Hosting.IStartupFilter, McpPathCollisionCheck>();

        // D13: the agent layers narrow every rights decision of an /mcp request, generic tool or not.
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IAgentRequestRestriction, McpAgentRestriction>());

        // What the account page and the consent screen may offer a user to grant (Abstractions seam, decision log M3a).
        services.AddScoped<MintPlayer.Spark.Abstractions.IAgentExposure, McpAgentExposure>();

        // An application tool runs a custom action through the web's own gates (D9; decision log, M5).
        services.AddScoped<ISparkMcpActions, McpCustomActions>();

        services.AddMcpServer(server =>
            {
                server.ServerInfo = new Implementation
                {
                    Name = options.ServerName,
                    Version = typeof(SparkMcpExtensions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0",
                };
                server.ServerInstructions = options.Instructions;
            })
            // D2: plain Stateless serves 2026-07-28 and 2025-11-25 clients alike, with no session affinity.
            .WithHttpTransport(transport => transport.SessionMode = HttpServerSessionMode.Stateless)
            .WithRequestFilters(filters =>
            {
                filters.AddListToolsFilter(McpToolFilters.ListTools);
                filters.AddCallToolFilter(McpToolFilters.CallTool);
            });

        // Writes (D8): propose_changes / apply_changes over plans in the McpPlans collection, which carry @expires
        // (10 minutes proposed, PlanRetention once applied). UseSpark() enables document expiration on the database;
        // a plan read checks its own expiry too, since the sweep may run long after it.
        services.AddScoped<Plans.McpPlanner>();
        builder.Registry.RequireDocumentExpiration("Mcp");

        McpToolRegistration.Register(services, options.ToolPrefix, [typeof(SparkGenericTools), typeof(SparkWriteTools), .. options.ToolTypes]);
        McpPromptRegistration.Register(services, options.ToolPrefix, options.PromptTypes);

        // UseSpark(), after authentication and authorization: resolve the setups (an explicit flag without its
        // wiring fails here, at startup), then gate every request the MCP endpoint will serve.
        builder.Registry.AddMiddleware(app =>
        {
            app.ApplicationServices.GetRequiredService<McpAuthentication>().Resolve(app.ApplicationServices, options);
            app.UseMiddleware<McpGateMiddleware>();
        });

        builder.Registry.AddEndpoints(endpoints =>
        {
            var authentication = endpoints.ServiceProvider.GetRequiredService<McpAuthentication>();
            var setups = authentication.Setups;
            var mcp = endpoints.MapMcp(options.NormalizedPath).WithMetadata(new SparkMcpEndpointMarker());

            // RFC 9728 (plan M3b, gap G2): where an agent learns which server signs it in. Generator endpoints, which map
            // themselves only with a setup that offers OAuth: at the path-inserted location the challenge names
            // (McpProtectedResourceDocument), and at the bare one some clients try first (McpProtectedResourceRootDocument).
            endpoints.MapSparkMcpEndpoints();

            // D3: only the bearer schemes authenticate /mcp. A policy OBJECT, not a registered name, so analyzer
            // SPARK020 has nothing to say (a name Spark never registers throws at request time; an object cannot).
            // It lets every caller through: an anonymous call is allowed and decided per tool. With no scheme wired
            // there is no policy (one naming no scheme would keep the cookie principal); the gate makes it anonymous.
            if (setups.Count > 0)
            {
                mcp.RequireAuthorization(new AuthorizationPolicyBuilder([.. setups.Select(s => s.Scheme)])
                    .RequireAssertion(static _ => true)
                    .Build());
            }
        });

        return builder;
    }

    private static void Validate(SparkMcpOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ServerName))
            throw new InvalidOperationException("mcp.ServerName is required: agents show it, and the tool prefix defaults to it.");

        var path = options.NormalizedPath;
        if (path == "/" || path.StartsWith("/spark", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"mcp.Path '{options.Path}' is not usable: it must be its own path, outside /spark (whose CORS and antiforgery defaults are for the web client).");

        if (options.ToolPrefix.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-')))
            throw new InvalidOperationException($"mcp.ToolPrefix '{options.ToolPrefix}' may contain only lower-case letters, digits, '_' and '-'.");

        ArgumentNullException.ThrowIfNull(options.AgentPermissions, "mcp.AgentPermissions");

        if (options.PlanRetention <= TimeSpan.Zero)
            throw new InvalidOperationException($"mcp.PlanRetention ({options.PlanRetention}) must be positive: applied plans are the record of what agents changed.");
    }
}
