using System;
using System.Text.RegularExpressions;

namespace MintPlayer.Spark.SourceGenerators.Json;

/// <summary>
/// Extracts the top-level metadata we need from a Spark Model JSON file
/// (<c>App_Data/Model/*.json</c>): the <c>persistentObject.id</c>,
/// <c>persistentObject.name</c>, and the optional <c>persistentObject.schema</c>.
///
/// <para>
/// Deliberately a minimal regex reader instead of a full JSON parser — the only
/// thing the generator needs is three top-level string values.
/// </para>
///
/// <para>
/// Convention: <c>id</c>, <c>name</c>, and <c>schema</c> appear before any nested
/// object or array in every Model JSON file. "First match wins" is therefore
/// safe — a nested <c>attributes[].id</c> never outranks the top-level
/// <c>persistentObject.id</c>.
/// </para>
///
/// <para>
/// Per R2-C5, <c>name</c> and <c>schema</c> are validated against the C#
/// identifier grammar before the entry is emitted: <c>AdditionalFiles</c>
/// content is untrusted (contributor PRs, generated templates), and the
/// downstream producer interpolates these strings directly as identifier
/// slots. A name like <c>"Foo; } class X { static X(){...} } class Bar"</c>
/// would otherwise compile attacker code into the host's emitted source.
/// </para>
/// </summary>
internal static class ModelJsonReader
{
    /// <summary>
    /// Returns <c>null</c> when the file cannot be parsed into a usable
    /// <see cref="Models.PersistentObjectIdInfo"/> — missing fields, invalid Guid,
    /// or no <c>persistentObject</c> wrapper at all.
    /// </summary>
    public static bool TryRead(string json, out Models.PersistentObjectIdInfo? info)
    {
        info = null;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        // Must be wrapped in a "persistentObject" root — protects against picking up
        // unrelated JSON files (translations.json, security.json, etc.) that happen
        // to share the AdditionalFiles include pattern.
        if (!PersistentObjectOpen.IsMatch(json))
            return false;

        var id = FindTopLevelString(json, "id");
        var name = FindTopLevelString(json, "name");
        if (id is null || name is null)
            return false;

        if (!Guid.TryParse(id, out _))
            return false;

        // R2-C5: identifier slot — reject entries that would inject arbitrary C#
        // into the generated source. Silent drop is intentional: bad-shape files
        // should fail closed rather than fail loud at compile time, because the
        // adversary's win condition is "anything I write reaches the compiler".
        if (!IsValidCSharpIdentifier(name))
            return false;

        var schema = FindTopLevelString(json, "schema");
        var resolvedSchema = string.IsNullOrWhiteSpace(schema) ? "Default" : schema!;
        if (!IsValidCSharpIdentifier(resolvedSchema))
            return false;

        info = new Models.PersistentObjectIdInfo
        {
            Id = id,
            Name = name,
            Schema = resolvedSchema,
        };
        return true;
    }

    private static bool IsValidCSharpIdentifier(string value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        if (!IsIdentifierStart(value[0])) return false;
        for (int i = 1; i < value.Length; i++)
            if (!IsIdentifierPart(value[i])) return false;
        return true;
    }

    private static bool IsIdentifierStart(char c) => c == '_' || char.IsLetter(c);
    private static bool IsIdentifierPart(char c) => c == '_' || char.IsLetterOrDigit(c);

    private static readonly Regex PersistentObjectOpen = new(
        "\"persistentObject\"\\s*:\\s*\\{",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string? FindTopLevelString(string json, string key)
    {
        // Matches: "key" : "value"  — allowing simple backslash escapes in the value.
        var pattern = "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"";
        var m = Regex.Match(json, pattern);
        return m.Success ? Unescape(m.Groups[1].Value) : null;
    }

    private static string Unescape(string s)
        => s.Replace("\\\"", "\"")
            .Replace("\\\\", "\\")
            .Replace("\\n", "\n")
            .Replace("\\r", "\r")
            .Replace("\\t", "\t");
}
