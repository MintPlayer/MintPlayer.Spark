using System.Text.Json;

namespace MintPlayer.Spark.Tests._Infrastructure;

/// <summary>
/// Reads a <c>TranslatedString</c> off the wire the way the client does, from its one shape
/// <c>{"Entries":[{"Tag":"en","Value":"…"}]}</c> (commerce X17). Strict on purpose: a response that
/// still sent the old flat <c>{"en":"…"}</c> fails here instead of being quietly accepted.
/// </summary>
internal static class TranslatedStringWire
{
    /// <summary>The value of the first entry tagged exactly <paramref name="tag"/>, or null.</summary>
    public static string? Text(this JsonElement translated, string tag)
    {
        foreach (var entry in translated.GetProperty("Entries").EnumerateArray())
            if (string.Equals(entry.GetProperty("Tag").GetString(), tag, StringComparison.OrdinalIgnoreCase))
                return entry.GetProperty("Value").GetString();
        return null;
    }
}
