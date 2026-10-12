using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Abstractions.Authentication;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MintPlayer.Spark.Mcp.Tools;

/// <summary>
/// The SDK request filters: <c>tools/list</c> shows only the tools the agent's permission covers, and
/// <c>tools/call</c> refuses the others exactly as it refuses an unknown name (MCP PRD §4.5, D6), then writes
/// one audit event per call (D15).
/// </summary>
internal static partial class McpToolFilters
{
    /// <summary>Per process: a hash an attacker with the log cannot reverse by hashing guesses (search terms can be personal data).</summary>
    private static readonly byte[] ArgumentsKey = RandomNumberGenerator.GetBytes(32);

    public static McpRequestHandler<ListToolsRequestParams, ListToolsResult> ListTools(McpRequestHandler<ListToolsRequestParams, ListToolsResult> next)
        => async (request, cancellationToken) =>
        {
            var result = await next(request, cancellationToken);
            var state = McpRequestState.Of(HttpContextOf(request));
            var granted = state?.Permissions ?? Abstractions.EAgentPermission.None;

            foreach (var tool in result.Tools.ToList())
            {
                var needs = McpToolRegistration.Needs(tool);
                if ((granted & needs) != needs)
                    result.Tools.Remove(tool);
            }
            return result;
        };

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> CallTool(McpRequestHandler<CallToolRequestParams, CallToolResult> next)
        => async (request, cancellationToken) =>
        {
            var context = HttpContextOf(request);
            var state = McpRequestState.Of(context);

            if (request.MatchedPrimitive is McpServerTool tool)
            {
                var needs = McpToolRegistration.Needs(tool.ProtocolTool);
                if (state is null || (state.Permissions & needs) != needs)
                {
                    // Byte-identical to the SDK's answer for a name it does not know: a tool the agent may not use
                    // is not there, rather than "there, but forbidden".
                    throw new McpProtocolException($"Unknown tool: '{request.Params?.Name}'", McpErrorCode.InvalidParams);
                }
            }

            var stopwatch = Stopwatch.StartNew();
            var outcome = "exception";
            try
            {
                var result = await next(request, cancellationToken);
                outcome = result.IsError == true ? "error" : "ok";
                if (state is { SignInRequired: true } && request.JsonRpcRequest?.Id is { } id)
                {
                    // The gate answers this call with an HTTP 401 instead (D17); its JSON-RPC error names the call.
                    outcome = "sign-in required";
                    state.JsonRpcId = JsonSerializer.SerializeToNode(id, McpJsonUtilities.DefaultOptions);
                }
                return result;
            }
            finally
            {
                Audit(request, context, state, outcome, stopwatch.Elapsed);
            }
        };

    private static void Audit(RequestContext<CallToolRequestParams> request, HttpContext? context, McpRequestState? state, string outcome, TimeSpan duration)
    {
        var services = context?.RequestServices;
        var logger = services?.GetService<ILoggerFactory>()?.CreateLogger("MintPlayer.Spark.Mcp.Audit");
        if (logger is null)
            return;

        LogCall(logger,
            request.Params?.Name,
            services!.GetService<ISparkCurrentUser>()?.Id,
            state?.Agent.ClientId,
            state?.Agent.Kind.ToString() ?? "unknown",
            state?.Agent.GrantId,
            HashArguments(request.Params?.Arguments),
            state?.RowCount,
            outcome,
            (long)duration.TotalMilliseconds);
    }

    [LoggerMessage(EventId = 1, EventName = "SparkMcpCall", Level = LogLevel.Information,
        Message = "MCP call {Tool} by user {UserId}, client {ClientId} ({AgentKind}), grant {GrantId}, arguments {ArgumentsHash}: {RowCount} rows, {Outcome} in {DurationMs} ms")]
    private static partial void LogCall(ILogger logger, string? tool, string? userId, string? clientId, string agentKind, string? grantId, string argumentsHash, int? rowCount, string outcome, long durationMs);

    /// <summary>A keyed hash of the arguments: equal calls log equal hashes within a process, and no argument is logged.</summary>
    internal static string HashArguments(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
            return "-";

        var canonical = JsonSerializer.Serialize(arguments.OrderBy(a => a.Key, StringComparer.Ordinal).ToDictionary(a => a.Key, a => a.Value));
        return Convert.ToHexStringLower(HMACSHA256.HashData(ArgumentsKey, Encoding.UTF8.GetBytes(canonical)))[..16];
    }

    private static HttpContext? HttpContextOf(MessageContext request)
        => request.Services?.GetService<IHttpContextAccessor>()?.HttpContext;
}
