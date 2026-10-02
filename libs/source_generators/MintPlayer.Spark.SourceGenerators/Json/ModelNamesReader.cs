using System;
using System.Collections.Generic;
using System.Text;

namespace MintPlayer.Spark.SourceGenerators.Json;

/// <summary>
/// Reads the names a security analyzer cross-references out of one <c>App_Data/Model/*.json</c>
/// file, by <em>position</em> rather than by key: the persistent object's own name and alias, its
/// attributes' names, the query names and aliases, and the lookup-reference types.
/// </summary>
/// <remarks>
/// <para>
/// Position matters. The previous regex matched every <c>"name"</c> in the file, so an attribute,
/// tab or group name counted as a known <em>type</em> target and <c>Read/Brand</c> (an attribute of
/// Car) passed SPARK012. Attribute-level rights also need the attributes <em>per type</em>.
/// </para>
/// <para>
/// A small recursive-descent reader over the full JSON grammar (objects, arrays, strings, numbers,
/// literals), for the reason <see cref="SecurityJsonReader"/> states: the analyzer is netstandard2.0
/// with no JSON library it can safely ship. Returns null on malformed input, and the caller falls
/// back to reporting nothing about that file.
/// </para>
/// </remarks>
internal static class ModelNamesReader
{
    internal sealed class ModelNames
    {
        public string? TypeName { get; set; }
        public string? Alias { get; set; }
        public string? ClrType { get; set; }
        public List<string> Attributes { get; } = new();

        /// <summary>Query names/aliases and lookup-reference types — targets, but not persistent objects.</summary>
        public List<string> OtherTargets { get; } = new();
    }

    /// <summary>
    /// Parses any JSON document into dictionaries, lists, strings, booleans and (as strings) numbers;
    /// null on malformed input.
    /// </summary>
    public static object? ParseOrNull(string json)
    {
        try
        {
            var pos = 0;
            return ParseValue(json.TrimStart('﻿'), ref pos);
        }
        catch (FormatException)
        {
            return null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>The declared groups' display names (first translation), by group id.</summary>
    public static Dictionary<string, string> ReadGroupNames(string securityJson)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (ParseOrNull(securityJson) is not Dictionary<string, object?> root
            || Get(root, "groups") is not Dictionary<string, object?> groups)
            return names;

        foreach (var pair in groups)
        {
            if (pair.Value is Dictionary<string, object?> translations)
            {
                foreach (var t in translations)
                {
                    if (t.Value is string name && name.Length > 0) { names[pair.Key] = name; break; }
                }
            }
            else if (pair.Value is string plain && plain.Length > 0)
            {
                names[pair.Key] = plain;
            }
        }

        return names;
    }

    public static ModelNames? Read(string json)
    {
        var root = ParseOrNull(json);

        if (root is not Dictionary<string, object?> file
            || Get(file, "persistentObject") is not Dictionary<string, object?> po)
            return null;

        var result = new ModelNames
        {
            TypeName = Get(po, "name") as string,
            Alias = Get(po, "alias") as string,
            ClrType = Get(po, "clrType") as string,
        };

        if (Get(po, "attributes") is List<object?> attributes)
        {
            foreach (var attribute in attributes)
            {
                if (attribute is not Dictionary<string, object?> a) continue;
                if (Get(a, "name") is string name && name.Length > 0) result.Attributes.Add(name);

                // A lookupReferenceType names a DynamicLookupReference class, a target like any other.
                if (Get(a, "lookupReferenceType") is string lookup && lookup.Length > 0)
                {
                    var dot = lookup.LastIndexOf('.');
                    result.OtherTargets.Add(dot < 0 ? lookup : lookup.Substring(dot + 1));
                }
            }
        }

        AddQueries(Get(file, "queries"), result.OtherTargets);
        AddQueries(Get(po, "queries"), result.OtherTargets);
        return result;
    }

    private static void AddQueries(object? queries, List<string> into)
    {
        if (queries is not List<object?> list) return;
        foreach (var query in list)
        {
            if (query is not Dictionary<string, object?> q) continue;
            if (Get(q, "name") is string name && name.Length > 0) into.Add(name);
            if (Get(q, "alias") is string alias && alias.Length > 0) into.Add(alias);
        }
    }

    private static object? Get(Dictionary<string, object?> obj, string key)
    {
        foreach (var pair in obj)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        }
        return null;
    }

    private static object? ParseValue(string s, ref int pos)
    {
        SkipWhitespace(s, ref pos);
        if (pos >= s.Length) throw new FormatException("Unexpected end of input.");

        switch (s[pos])
        {
            case '{': return ParseObject(s, ref pos);
            case '[': return ParseArray(s, ref pos);
            case '"': return ParseString(s, ref pos);
            case 't': Expect(s, ref pos, "true"); return true;
            case 'f': Expect(s, ref pos, "false"); return false;
            case 'n': Expect(s, ref pos, "null"); return null;
            default:
                var start = pos;
                while (pos < s.Length && (char.IsDigit(s[pos]) || s[pos] is '-' or '+' or '.' or 'e' or 'E')) pos++;
                if (pos == start) throw new FormatException($"Unexpected character at offset {pos}.");
                return s.Substring(start, pos - start);
        }
    }

    private static Dictionary<string, object?> ParseObject(string s, ref int pos)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        pos++; // {
        SkipWhitespace(s, ref pos);
        if (s[pos] == '}') { pos++; return result; }

        while (true)
        {
            SkipWhitespace(s, ref pos);
            var key = ParseString(s, ref pos);
            SkipWhitespace(s, ref pos);
            if (s[pos] != ':') throw new FormatException($"Expected ':' at offset {pos}.");
            pos++;
            result[key] = ParseValue(s, ref pos);
            SkipWhitespace(s, ref pos);
            if (s[pos] == ',') { pos++; continue; }
            if (s[pos] == '}') { pos++; return result; }
            throw new FormatException($"Expected ',' or '}}' at offset {pos}.");
        }
    }

    private static List<object?> ParseArray(string s, ref int pos)
    {
        var result = new List<object?>();
        pos++; // [
        SkipWhitespace(s, ref pos);
        if (s[pos] == ']') { pos++; return result; }

        while (true)
        {
            result.Add(ParseValue(s, ref pos));
            SkipWhitespace(s, ref pos);
            if (s[pos] == ',') { pos++; continue; }
            if (s[pos] == ']') { pos++; return result; }
            throw new FormatException($"Expected ',' or ']' at offset {pos}.");
        }
    }

    private static string ParseString(string s, ref int pos)
    {
        if (s[pos] != '"') throw new FormatException($"Expected '\"' at offset {pos}.");
        pos++;
        var sb = new StringBuilder();
        while (pos < s.Length)
        {
            var c = s[pos++];
            if (c == '"') return sb.ToString();
            if (c != '\\') { sb.Append(c); continue; }

            var esc = s[pos++];
            switch (esc)
            {
                case 'u':
                    sb.Append((char)Convert.ToInt32(s.Substring(pos, 4), 16));
                    pos += 4;
                    break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                default: sb.Append(esc); break;
            }
        }
        throw new FormatException("Unterminated string.");
    }

    private static void Expect(string s, ref int pos, string literal)
    {
        if (string.CompareOrdinal(s, pos, literal, 0, literal.Length) != 0)
            throw new FormatException($"Expected '{literal}' at offset {pos}.");
        pos += literal.Length;
    }

    private static void SkipWhitespace(string s, ref int pos)
    {
        while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
    }
}
