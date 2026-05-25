using Newtonsoft.Json;
using System.Text;

namespace System.Net.WebSockets;

public static class SocketExtensions
{
    const int bufferSize = 512;

    /// <summary>
    /// Default maximum bytes <see cref="ReadMessage(WebSocket, int)"/> will buffer
    /// before closing the socket with <see cref="WebSocketCloseStatus.MessageTooBig"/>.
    /// 1 MiB is sized for handshake/control payloads; callers expecting larger frames
    /// pass an explicit limit.
    /// </summary>
    public const int DefaultMaxMessageBytes = 1 * 1024 * 1024;

    public static async Task<string> ReadMessage(this WebSocket ws, int maxBytes = DefaultMaxMessageBytes)
    {
        var buffer = new ArraySegment<byte>(new byte[bufferSize]);
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;

        do
        {
            result = await ws.ReceiveAsync(buffer, CancellationToken.None);
            // R2-H13: an unauthenticated WebSocket caller used to be able to stream
            // an unbounded message that the server would buffer in memory in full
            // before deciding whether to act on it — a handful of parallel
            // connections OOMs the process. We now refuse anything past maxBytes,
            // closing the socket cleanly so the caller knows the reason.
            if (ms.Length + result.Count > maxBytes)
            {
                try
                {
                    await ws.CloseAsync(WebSocketCloseStatus.MessageTooBig,
                        $"Message exceeded {maxBytes} bytes", CancellationToken.None);
                }
                catch { /* socket may already be closing */ }
                throw new WebSocketException(WebSocketError.NotAWebSocket,
                    $"Inbound message exceeded {maxBytes} bytes");
            }
            ms.Write(buffer.Array ?? Array.Empty<byte>(), buffer.Offset, result.Count);
        }
        while (!result.EndOfMessage);

        if (result.MessageType == WebSocketMessageType.Close)
        {
            throw new WebSocketException("The websocket was closed");
        }

        ms.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(ms, Encoding.UTF8);
        var message = await reader.ReadToEndAsync();
        return message;
    }

    public static async Task WriteMessage(this WebSocket ws, string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        var bytesSent = 0;

        do
        {
            var arraySegment = new ArraySegment<byte>(bytes, bytesSent, Math.Min(bufferSize, bytes.Length - bytesSent));
            bytesSent += bufferSize;
            await ws.SendAsync(arraySegment, WebSocketMessageType.Text, bytesSent >= bytes.Length, CancellationToken.None);
        }
        while (bytesSent < bytes.Length);
    }

    public static async Task<T?> ReadObject<T>(this WebSocket ws)
    {
        var message = await ws.ReadMessage();
        var obj = JsonConvert.DeserializeObject<T>(message);
        return obj;
    }

    public static async Task WriteObject<T>(this WebSocket ws, T obj)
    {
        var json = JsonConvert.SerializeObject(obj);
        await ws.WriteMessage(json);
    }
}
