using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SparkEditor.Services;

namespace SparkEditor.Endpoints;

public static class FileEventsEndpoint
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static void MapFileEventsEndpoint(this WebApplication app)
    {
        app.Map("/spark/editor/file-events", HandleAsync);
    }

    private static async Task HandleAsync(HttpContext httpContext, ISparkEditorFileService fileService)
    {
        if (!httpContext.WebSockets.IsWebSocketRequest)
        {
            httpContext.Response.StatusCode = 400;
            await httpContext.Response.WriteAsync("WebSocket connection expected");
            return;
        }

        using var ws = await httpContext.WebSockets.AcceptWebSocketAsync();

        void handler(object? s, FileChangedEventArgs e)
        {
            if (ws.State != WebSocketState.Open) return;

            var message = new FileChangedMessage
            {
                Type = "fileChanged",
                FilePath = e.FilePath,
                FileName = Path.GetFileName(e.FilePath),
                ChangeType = e.ChangeType.ToString(),
                AffectedEntities = ResolveAffectedEntities(e.FilePath)
            };

            var json = JsonSerializer.Serialize(message, JsonOptions);
            var bytes = Encoding.UTF8.GetBytes(json);

            // Fire-and-forget send; if it fails the connection is closing
            try
            {
                var segment = new ArraySegment<byte>(bytes);
                ws.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None)
                    .ContinueWith(_ => { }, TaskScheduler.Default);
            }
            catch (WebSocketException) { }
        }

        fileService.FileChanged += handler;

        try
        {
            // Keep connection open, listen for close frames
            var buffer = new byte[256];
            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, httpContext.RequestAborted);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            fileService.FileChanged -= handler;
            if (ws.State == WebSocketState.Open)
            {
                try
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                }
                catch { }
            }
        }
    }

    private static string[] ResolveAffectedEntities(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        var dir = Path.GetDirectoryName(filePath) ?? "";

        if (dir.EndsWith("Model", StringComparison.OrdinalIgnoreCase))
            return ["PersistentObjectDefinition", "AttributeDefinition", "QueryDefinition"];

        return fileName.ToLowerInvariant() switch
        {
            "programunits.json" => ["ProgramUnitGroupDef", "ProgramUnitDef"],
            "customactions.json" => ["CustomActionDef"],
            "security.json" => ["SecurityGroupDef", "SecurityRightDef"],
            "culture.json" => ["LanguageDef"],
            "translations.json" => ["TranslationDef"],
            _ => []
        };
    }
}

internal class FileChangedMessage
{
    public string Type { get; set; } = "fileChanged";
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string ChangeType { get; set; } = string.Empty;
    public string[] AffectedEntities { get; set; } = [];
}
