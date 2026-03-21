using Newtonsoft.Json;
using System.IO;
using System.Net.WebSockets;
using System.Text;

namespace System.Net.WebSockets;

public static class SocketExtensions
{
    const int bufferSize = 512;

    public static async Task<string> ReadMessage(this WebSocket ws)
    {
        var buffer = new ArraySegment<byte>(new byte[bufferSize]);
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;

        do
        {
            result = await ws.ReceiveAsync(buffer, CancellationToken.None);
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
