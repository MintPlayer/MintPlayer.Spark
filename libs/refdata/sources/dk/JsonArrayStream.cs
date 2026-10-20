using System.Text.Json;

namespace MintPlayer.Spark.ReferenceData.Sources.Denmark;

/// <summary>
/// Streams the objects of a JSON document shaped <c>{ "AList": [ {…}, … ], "BList": [ … ] }</c> (Datafordeler's file
/// downloads), one object at a time with its array's name, without loading the document: memory stays flat however
/// large the file (RS1). Downloads are data: the reader only parses.
/// </summary>
internal static class JsonArrayStream
{
    public static IEnumerable<(string Array, JsonElement Item)> Read(Stream stream)
    {
        var buffer = new byte[1 << 16];
        var length = 0;
        var final = false;
        var state = new JsonReaderState(new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
        string? array = null;
        while (true)
        {
            if (!final && length < buffer.Length)
            {
                var read = stream.Read(buffer, length, buffer.Length - length);
                if (read == 0) final = true;
                length += read;
            }
            var (batch, consumed, nextState, nextArray, needMore) = Scan(buffer.AsSpan(0, length), final, state, array);
            foreach (var item in batch)
                yield return item;
            state = nextState;
            array = nextArray;
            Buffer.BlockCopy(buffer, consumed, buffer, 0, length - consumed);
            length -= consumed;
            if (final && consumed == 0 && !needMore) yield break;
            if (needMore && length == buffer.Length)
                Array.Resize(ref buffer, buffer.Length * 2); // one object larger than the buffer
            if (final && length == 0) yield break;
            if (final && needMore && consumed == 0)
                throw new InvalidDataException("The JSON document ends inside an object.");
        }
    }

    private static (List<(string, JsonElement)> Batch, int Consumed, JsonReaderState State, string? Array, bool NeedMore) Scan(ReadOnlySpan<byte> data, bool final, JsonReaderState state, string? array)
    {
        var batch = new List<(string, JsonElement)>();
        var reader = new Utf8JsonReader(data, final, state);
        var consumed = 0;
        var savedState = state;
        while (true)
        {
            var before = reader.CurrentState;
            var beforeConsumed = (int)reader.BytesConsumed;
            if (!reader.Read())
                return (batch, consumed, savedState, array, false);
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName when reader.CurrentDepth == 1:
                    array = reader.GetString();
                    break;
                case JsonTokenType.StartObject when reader.CurrentDepth == 2 && array is not null:
                    var start = beforeConsumed;
                    if (!reader.TrySkip())
                        return (batch, beforeConsumed, before, array, true);
                    // The bytes of one complete object, from its '{' (after any separator) to its '}'.
                    var slice = data[start..(int)reader.BytesConsumed];
                    var open = slice.IndexOf((byte)'{');
                    using (var document = JsonDocument.Parse(slice[open..].ToArray()))
                        batch.Add((array, document.RootElement.Clone()));
                    break;
            }
            consumed = (int)reader.BytesConsumed;
            savedState = reader.CurrentState;
        }
    }
}
