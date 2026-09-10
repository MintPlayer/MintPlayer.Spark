using Microsoft.Extensions.Primitives;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;

namespace MintPlayer.Spark.Webhooks.GitHub.DevTunnel.Services;

/// <summary>
/// Turns one smee.io SSE frame into the headers and the <em>exact bytes GitHub signed</em>.
/// <para>
/// <b>The rule this type exists to enforce: never reconstruct the signed body — capture it.</b>
/// GitHub signs the body it sent. Any path that parses that body into an object model and
/// re-serializes it has already lost, however carefully it re-serializes, because the parse is
/// lossy in ways serialization cannot undo.
/// </para>
/// <para>
/// This is not theoretical. The previous implementation went through <c>Smee.IO.Client</c>, whose
/// <c>SmeeData.Body</c> is typed <see cref="object"/> and materializes as a Newtonsoft
/// <c>JObject</c>. Measured against that library, the body GitHub signed as
/// <c>{"created_at":"2024-01-01T12:00:12.000+02:00","amount":1.50}</c> came back as
/// <c>{"created_at":"2024-01-01T11:00:12+01:00","amount":1.5}</c> — the decimal lost its trailing
/// zero and the timestamp was rebased into the <em>host machine's local timezone</em>, so the
/// corruption even differed per developer. Every such delivery failed HMAC validation and was
/// silently dropped. Swapping the re-serializer for a lexical minifier produced byte-identical,
/// equally wrong output, because the damage happened before our code ran.
/// </para>
/// <para>
/// So the body is lifted straight out of the frame with
/// <see cref="JsonElement.GetRawText"/>, which returns the original text span.
/// See <c>docs/prd/PRD-SlingBot-Archival-Followups.md</c> §1 for the full measurement.
/// </para>
/// </summary>
internal static class SmeeBodyReader
{
    /// <summary>Envelope fields smee adds itself; everything else doubles as a header.</summary>
    private static readonly string[] EnvelopeFields = ["body", "query", "timestamp"];

    /// <summary>
    /// Parses one SSE frame. Returns <see langword="false"/> for smee's own ready/ping frames and
    /// anything else that is not a webhook delivery.
    /// </summary>
    public static bool TryReadDelivery(
        string frame,
        [NotNullWhen(true)] out Dictionary<string, StringValues>? headers,
        [NotNullWhen(true)] out string? body)
    {
        headers = null;
        body = null;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(frame);
        }
        catch (JsonException)
        {
            return false; // smee's ready/ping frames aren't always JSON objects
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("body", out var bodyElement)
                || !root.TryGetProperty("x-github-event", out _))
                return false;

            headers = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in root.EnumerateObject())
            {
                if (EnvelopeFields.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                    continue;
                if (property.Value.ValueKind is JsonValueKind.String)
                    headers[property.Name] = new StringValues(property.Value.GetString());
            }

            // GetRawText() is the load-bearing call: it hands back the original characters, so
            // 1.50 stays 1.50 and a fractional-second offset keeps its offset.
            body = LexicalMinify(bodyElement.GetRawText());
            return true;
        }
    }

    /// <summary>
    /// Removes whitespace outside string literals; everything inside strings — escape sequences
    /// included — is copied verbatim, and no token is ever reinterpreted.
    /// <para>
    /// smee currently emits the envelope as a single minified line, so in practice this is a no-op
    /// and the raw text is already what GitHub signed. It is kept because pretty-printing is the
    /// one transformation a relay can apply that is genuinely reversible: if some future smee (or
    /// an intermediary) formats the envelope, stripping insignificant whitespace still reconstructs
    /// the exact bytes, where a parse/re-serialize would not.
    /// </para>
    /// </summary>
    public static string LexicalMinify(string json)
    {
        var result = new StringBuilder(json.Length);
        var inString = false;

        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];
            if (inString)
            {
                result.Append(c);
                if (c == '\\' && i + 1 < json.Length) result.Append(json[++i]);
                else if (c == '"') inString = false;
            }
            else if (c == '"')
            {
                inString = true;
                result.Append(c);
            }
            else if (!char.IsWhiteSpace(c))
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }
}
