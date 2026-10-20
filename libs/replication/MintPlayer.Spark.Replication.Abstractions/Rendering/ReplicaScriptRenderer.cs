using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace MintPlayer.Spark.Replication.Abstractions.Rendering;

// ⚠️ Linked into MintPlayer.Spark.LibraryGenerators (netstandard2.0). The generator and the owner must
// produce the same bytes from the same manifest, which is why this is one file and not two: keep it free
// of System.Text.Json, GeneratedRegex, Convert.ToHexString, ranges and Span-only APIs.

/// <summary>
/// Renders a <see cref="ReplicaManifest"/> to its canonical JSON, its RavenDB ETL script and their hash, and
/// parses the JSON back.
/// </summary>
/// <remarks>
/// Every name that reaches the script is checked against <c>^[A-Za-z_][A-Za-z0-9_]*$</c> first, so the
/// script can only ever be member accesses and one <c>loadTo</c> call: a manifest cannot smuggle JavaScript
/// in (F16). The parser is strict for the same reason: it accepts exactly the grammar
/// <see cref="ToJson"/> writes, and refuses everything else rather than ignoring it.
/// </remarks>
public static class ReplicaScriptRenderer
{
    private const string Indent = "    ";

    /// <summary>Whether <paramref name="value"/> is a plain ASCII identifier, <c>^[A-Za-z_][A-Za-z0-9_]*$</c>.</summary>
    public static bool IsIdentifier(string? value)
    {
        if (value is null || value.Length == 0)
            return false;

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var letter = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_';
            if (!letter && (i == 0 || c < '0' || c > '9'))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Why <paramref name="manifest"/> cannot be rendered, or <see langword="null"/> when it can.
    /// </summary>
    public static string? Validate(ReplicaManifest manifest)
    {
        if (manifest is null)
            throw new ArgumentNullException(nameof(manifest));

        if (!IsIdentifier(manifest.SourceCollection))
            return $"the source collection '{manifest.SourceCollection}' is not a plain identifier";
        if (!IsIdentifier(manifest.TargetCollection))
            return $"the target collection '{manifest.TargetCollection}' is not a plain identifier";
        if (manifest.Fields.Count == 0)
            return "it maps no field";

        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in manifest.Fields)
        {
            if (!IsIdentifier(field.Target))
                return $"the field '{field.Target}' is not a plain identifier";
            if (field.Target == "Id")
                return "the field 'Id' is mapped, but the document id is never part of a replica's body";
            if (!targets.Add(field.Target))
                return $"the field '{field.Target}' is mapped twice";
            if (field.Path.Count == 0)
                return $"the field '{field.Target}' has an empty source path";
            foreach (var segment in field.Path)
            {
                if (!IsIdentifier(segment))
                    return $"the source path '{field.PathText}' of '{field.Target}' has a segment '{segment}' that is not a plain identifier";
            }
        }

        return null;
    }

    /// <summary>
    /// The canonical JSON form, without whitespace:
    /// <c>{"v":1,"source":"Cars","target":"Cars","fields":[["LicensePlate","LicensePlate"],["OwnerName","Owner.Name"]]}</c>.
    /// Fields keep their declaration order; a path is joined with dots.
    /// </summary>
    public static string ToJson(ReplicaManifest manifest)
    {
        if (manifest is null)
            throw new ArgumentNullException(nameof(manifest));

        var json = new StringBuilder();
        json.Append("{\"v\":").Append(ReplicaManifest.FormatVersion);
        json.Append(",\"source\":");
        AppendString(json, manifest.SourceCollection);
        json.Append(",\"target\":");
        AppendString(json, manifest.TargetCollection);
        json.Append(",\"fields\":[");
        for (var i = 0; i < manifest.Fields.Count; i++)
        {
            if (i > 0)
                json.Append(',');
            json.Append('[');
            AppendString(json, manifest.Fields[i].Target);
            json.Append(',');
            AppendString(json, manifest.Fields[i].PathText);
            json.Append(']');
        }
        json.Append("]}");
        return json.ToString();
    }

    /// <summary>
    /// Parses what <see cref="ToJson"/> writes, then <see cref="Validate"/>s it. Whitespace between tokens is
    /// tolerated; keys must appear in the canonical order, strings may escape only <c>\"</c> and <c>\\</c>,
    /// and anything else (another key, another version, a trailing token) is refused.
    /// </summary>
    public static bool TryParse(string? json, out ReplicaManifest? manifest, out string? error)
    {
        manifest = null;
        if (json is null)
        {
            error = "the manifest is empty";
            return false;
        }

        var reader = new Reader(json);
        if (!reader.TryReadManifest(out var parsed))
        {
            error = reader.Error;
            return false;
        }

        error = Validate(parsed!);
        if (error is not null)
            return false;

        manifest = parsed;
        return true;
    }

    /// <summary>
    /// The module-independent ETL transform: one <c>loadTo{TargetCollection}</c> call writing every field,
    /// nested paths read null-safely, and the source change vector in the replica's metadata (WD18).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="manifest"/> fails <see cref="Validate"/>.</exception>
    public static string RenderScript(ReplicaManifest manifest)
        => Render(manifest, sourceModule: null);

    /// <summary>
    /// <see cref="RenderScript"/> plus the <c>Spark-Replica-Source-Module</c> metadata, for the ETL task one
    /// owner module deploys.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="manifest"/> fails <see cref="Validate"/>, or <paramref name="sourceModule"/> is not
    /// <c>^[A-Za-z0-9_.-]+$</c>.
    /// </exception>
    public static string RenderEtlScript(ReplicaManifest manifest, string sourceModule)
    {
        if (!IsModuleName(sourceModule))
            throw new ArgumentException($"The source module '{sourceModule}' must match ^[A-Za-z0-9_.-]+$.", nameof(sourceModule));

        return Render(manifest, sourceModule);
    }

    /// <summary>
    /// <see cref="RenderEtlScript(ReplicaManifest, string)"/> restricted to the rows <paramref name="scope"/> admits
    /// (warehouse §4.2.3, "Row filter"): the <c>loadTo</c> call runs only when every condition's field holds one of its
    /// values. The scope is the owner's, rendered at deploy; values are written as escaped string literals, fields are
    /// identifiers. A <see langword="null"/> scope renders the unfiltered script.
    /// </summary>
    public static string RenderEtlScript(ReplicaManifest manifest, string sourceModule, ReplicaRowScope? scope)
    {
        var script = RenderEtlScript(manifest, sourceModule);
        if (scope is null)
            return script;

        var condition = new StringBuilder();
        for (var i = 0; i < scope.Conditions.Count; i++)
        {
            var row = scope.Conditions[i];
            if (i > 0)
                condition.Append(" && ");
            condition.Append('[');
            for (var v = 0; v < row.AllowedValues.Count; v++)
            {
                if (v > 0)
                    condition.Append(", ");
                condition.Append(JavaScriptString(row.AllowedValues[v]));
            }
            // A field is an identifier (checked by ReplicaRowCondition); indexOf compares strictly, so a non-string never matches.
            condition.Append("].indexOf(this.").Append(row.Field).Append(") >= 0");
        }

        return "if (" + condition + ") {\n" + script + "\n}";
    }

    /// <summary>
    /// <paramref name="value"/> as a double-quoted JavaScript string literal. Everything but ASCII letters, digits and
    /// <c> _-./:@</c> is written as a <c>\uXXXX</c> escape, so no value can end the literal or the script.
    /// </summary>
    public static string JavaScriptString(string value)
    {
        if (value is null)
            throw new ArgumentNullException(nameof(value));

        const string hex = "0123456789abcdef";
        var literal = new StringBuilder(value.Length + 2);
        literal.Append('"');
        foreach (var c in value)
        {
            var plain = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
                || c == ' ' || c == '_' || c == '-' || c == '.' || c == '/' || c == ':' || c == '@';
            if (plain)
                literal.Append(c);
            else
                literal.Append("\\u").Append(hex[(c >> 12) & 0xF]).Append(hex[(c >> 8) & 0xF]).Append(hex[(c >> 4) & 0xF]).Append(hex[c & 0xF]);
        }
        literal.Append('"');
        return literal.ToString();
    }

    /// <summary>
    /// <c>"sha256:"</c> and the lowercase hex SHA-256 of the UTF-8 bytes of
    /// <c>manifestJson + "\n" + script</c>. Both sides compare it, so an unchanged script is neither posted
    /// nor redeployed (WD15).
    /// </summary>
    public static string Hash(string manifestJson, string script)
    {
        if (manifestJson is null)
            throw new ArgumentNullException(nameof(manifestJson));
        if (script is null)
            throw new ArgumentNullException(nameof(script));

        byte[] digest;
        using (var sha = SHA256.Create())
            digest = sha.ComputeHash(Encoding.UTF8.GetBytes(manifestJson + "\n" + script));

        const string hex = "0123456789abcdef";
        var text = new StringBuilder("sha256:", 7 + digest.Length * 2);
        foreach (var b in digest)
            text.Append(hex[b >> 4]).Append(hex[b & 0xF]);
        return text.ToString();
    }

    private static string Render(ReplicaManifest manifest, string? sourceModule)
    {
        if (manifest is null)
            throw new ArgumentNullException(nameof(manifest));

        var invalid = Validate(manifest);
        if (invalid is not null)
            throw new ArgumentException($"The replica manifest cannot be rendered: {invalid}.", nameof(manifest));

        var script = new StringBuilder();
        script.Append("loadTo").Append(manifest.TargetCollection).Append("({\n");
        foreach (var field in manifest.Fields)
            script.Append(Indent).Append(field.Target).Append(": ").Append(Access("this", field.Path, 0)).Append(",\n");

        script.Append(Indent).Append("'@metadata': { '").Append(ReplicaMetadata.SourceChangeVector)
            .Append("': getMetadata(this)['@change-vector']");
        if (sourceModule is not null)
            script.Append(", '").Append(ReplicaMetadata.SourceModule).Append("': '").Append(sourceModule).Append('\'');
        script.Append(" }\n");
        script.Append("});");
        return script.ToString();
    }

    /// <summary>
    /// <c>this.A</c> for a one-segment path; for longer ones every intermediate member is null-checked:
    /// <c>(this.A == null ? null : (this.A.B == null ? null : this.A.B.C))</c>.
    /// </summary>
    private static string Access(string prefix, IReadOnlyList<string> path, int index)
    {
        var member = prefix + "." + path[index];
        if (index == path.Count - 1)
            return member;

        return "(" + member + " == null ? null : " + Access(member, path, index + 1) + ")";
    }

    private static bool IsModuleName(string? value)
    {
        if (value is null || value.Length == 0)
            return false;

        foreach (var c in value)
        {
            var allowed = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
                || c == '_' || c == '.' || c == '-';
            if (!allowed)
                return false;
        }

        return true;
    }

    private static void AppendString(StringBuilder json, string value)
    {
        json.Append('"');
        foreach (var c in value)
        {
            if (c == '"' || c == '\\')
                json.Append('\\');
            json.Append(c);
        }
        json.Append('"');
    }

    /// <summary>A recursive-descent reader for exactly the grammar <see cref="ToJson"/> writes.</summary>
    private sealed class Reader
    {
        private readonly string text;
        private int position;

        public Reader(string text)
        {
            this.text = text;
        }

        public string? Error { get; private set; }

        public bool TryReadManifest(out ReplicaManifest? manifest)
        {
            manifest = null;

            if (!Expect('{') || !ExpectKey("v") || !TryReadVersion())
                return false;
            if (!Expect(',') || !ExpectKey("source") || !TryReadString(out var source))
                return false;
            if (!Expect(',') || !ExpectKey("target") || !TryReadString(out var target))
                return false;
            if (!Expect(',') || !ExpectKey("fields") || !Expect('['))
                return false;

            var fields = new List<ReplicaField>();
            SkipWhitespace();
            if (Peek() != ']')
            {
                while (true)
                {
                    if (!Expect('[') || !TryReadString(out var field) || !Expect(',') || !TryReadString(out var path) || !Expect(']'))
                        return false;
                    fields.Add(new ReplicaField(field!, path!.Split('.')));

                    SkipWhitespace();
                    if (Peek() != ',')
                        break;
                    position++;
                }
            }

            if (!Expect(']') || !Expect('}'))
                return false;

            SkipWhitespace();
            if (position != text.Length)
                return Fail("unexpected content after the manifest");

            manifest = new ReplicaManifest(source!, target!, fields);
            return true;
        }

        private bool TryReadVersion()
        {
            SkipWhitespace();
            var start = position;
            while (position < text.Length && text[position] >= '0' && text[position] <= '9')
                position++;

            if (position == start)
                return Fail("the version is not a number");

            var digits = text.Substring(start, position - start);
            if (digits != ReplicaManifest.FormatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))
                return Fail($"the manifest version {digits} is not supported (expected {ReplicaManifest.FormatVersion})");
            return true;
        }

        private bool ExpectKey(string key)
        {
            var at = position;
            if (!TryReadString(out var actual))
                return false;
            if (actual != key)
            {
                position = at;
                return Fail($"expected the key \"{key}\" but found \"{actual}\"");
            }
            return Expect(':');
        }

        private bool TryReadString(out string? value)
        {
            value = null;
            if (!Expect('"'))
                return false;

            var builder = new StringBuilder();
            while (true)
            {
                if (position >= text.Length)
                    return Fail("unterminated string");

                var c = text[position++];
                if (c == '"')
                    break;
                if (c < ' ')
                    return Fail("a control character in a string");
                if (c == '\\')
                {
                    if (position >= text.Length || (text[position] != '"' && text[position] != '\\'))
                        return Fail("an escape other than \\\" or \\\\ in a string");
                    c = text[position++];
                }
                builder.Append(c);
            }

            value = builder.ToString();
            return true;
        }

        private bool Expect(char expected)
        {
            SkipWhitespace();
            if (Peek() != expected)
                return Fail(position < text.Length
                    ? $"expected '{expected}' at offset {position} but found '{text[position]}'"
                    : $"expected '{expected}' at the end of the manifest");
            position++;
            return true;
        }

        private char Peek() => position < text.Length ? text[position] : '\0';

        private void SkipWhitespace()
        {
            while (position < text.Length && (text[position] == ' ' || text[position] == '\t' || text[position] == '\r' || text[position] == '\n'))
                position++;
        }

        private bool Fail(string error)
        {
            Error ??= error;
            return false;
        }
    }
}
