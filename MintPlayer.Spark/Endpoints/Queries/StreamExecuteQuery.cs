using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Streaming;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace MintPlayer.Spark.Endpoints.Queries;

internal sealed partial class StreamExecuteQuery : IEndpoint, IMemberOf<QueriesGroup>
{
    public static string Path => "/{id}/stream";
    public static IEnumerable<string> Methods => ["GET"];

    [Inject] private readonly IQueryLoader queryLoader;
    [Inject] private readonly IStreamingQueryExecutor streamingQueryExecutor;

    private static readonly JsonSerializerOptions jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var id = httpContext.Request.RouteValues["id"]!.ToString()!;

        if (!httpContext.WebSockets.IsWebSocketRequest)
        {
            return Results.Json(new { error = "WebSocket connection required" }, statusCode: 400);
        }

        var query = queryLoader.ResolveQuery(id);
        if (query is null)
        {
            return Results.Json(new { error = $"Query '{id}' not found" }, statusCode: 404);
        }

        if (!query.IsStreamingQuery)
        {
            return Results.Json(new { error = $"Query '{id}' is not a streaming query" }, statusCode: 400);
        }

        using var webSocket = await httpContext.WebSockets.AcceptWebSocketAsync();
        var cancellationToken = httpContext.RequestAborted;
        var diffEngine = new StreamingDiffEngine();

        try
        {
            await foreach (var items in streamingQueryExecutor.ExecuteStreamingQueryAsync(query, cancellationToken))
            {
                var message = diffEngine.ComputeMessage(items);
                if (message is null) continue;

                var json = JsonSerializer.Serialize<StreamingMessage>(message, jsonOptions);
                var bytes = Encoding.UTF8.GetBytes(json);

                if (webSocket.State != WebSocketState.Open) break;

                await webSocket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken);
            }

            // Streaming method completed normally — close the WebSocket
            if (webSocket.State == WebSocketState.Open)
            {
                await webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Stream completed",
                    CancellationToken.None);
            }
        }
        catch (SparkAccessDeniedException)
        {
            await SendErrorAndCloseAsync(webSocket, "Access denied");
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — silently exit
        }
        catch (WebSocketException)
        {
            // WebSocket error (likely client disconnect) — silently exit
        }
        catch (Exception ex)
        {
            await SendErrorAndCloseAsync(webSocket, ex.Message);
        }

        return Results.Empty;
    }

    private static async Task SendErrorAndCloseAsync(WebSocket webSocket, string message)
    {
        if (webSocket.State != WebSocketState.Open) return;

        try
        {
            var errorMessage = new ErrorMessage { Message = message };
            var json = JsonSerializer.Serialize<StreamingMessage>(errorMessage, jsonOptions);
            var bytes = Encoding.UTF8.GetBytes(json);

            await webSocket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                CancellationToken.None);

            await webSocket.CloseAsync(
                WebSocketCloseStatus.InternalServerError,
                message,
                CancellationToken.None);
        }
        catch
        {
            // Best-effort error delivery — ignore failures
        }
    }
}
