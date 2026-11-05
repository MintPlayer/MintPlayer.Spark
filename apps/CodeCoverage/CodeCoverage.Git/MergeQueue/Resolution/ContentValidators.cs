using System.Text;
using System.Text.Json;
using System.Xml;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.SourceGenerators.Attributes;

namespace CodeCoverage.Git.MergeQueue.Resolution;

// The content validators of §10.4 step 4 (O33). Each is pure and deterministic, reads nothing but its text, and names a
// problem by kind and position only: the verdict reaches an eviction message on the pull request, so it never quotes the
// content.

/// <summary><c>utf8</c>: well-formed text without a NUL. Strict UTF-8 decoding itself happens when a blob is read
/// (<see cref="ConflictRepository.DecodeStrict"/>); this catches what a string can still hold, a NUL or a lone
/// surrogate.</summary>
[Register(typeof(IContentValidator), ServiceLifetime.Singleton)]
public sealed class Utf8Validator : IContentValidator
{
    public string Name => MergeQueueWireRules.Utf8;

    public ContentVerdict Validate(string text)
    {
        long line = 1, column = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            column++;
            if (c == '\n')
            {
                line++;
                column = 0;
                continue;
            }
            if (c == '\0')
                return ContentVerdict.Invalid("a NUL character", line, column);
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
                continue;
            }
            if (char.IsSurrogate(c))
                return ContentVerdict.Invalid("a character that is not valid UTF-8", line, column);
        }
        return ContentVerdict.Ok;
    }
}

/// <summary>
/// <c>json</c>: one strict RFC 8259 value. No comments, no trailing commas, nothing after the value, at most
/// <see cref="MaxDepth"/> levels of nesting. A leading byte-order mark is allowed (RFC 8259 §8.1 lets a parser ignore it).
/// </summary>
[Register(typeof(IContentValidator), ServiceLifetime.Singleton)]
public sealed class JsonValidator : IContentValidator
{
    public const int MaxDepth = 64;

    public string Name => MergeQueueWireRules.Json;

    public ContentVerdict Validate(string text) => Check(text, duplicates: false);

    /// <summary>Reads the whole value; with <paramref name="duplicates"/>, also refuses two members of one object whose
    /// names are equal once unescaped (ordinal), at every depth.</summary>
    internal static ContentVerdict Check(string text, bool duplicates)
    {
        var bytes = Encoding.UTF8.GetBytes(text.StartsWith('﻿') ? text[1..] : text);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            AllowMultipleValues = false,
            MaxDepth = MaxDepth,
        });
        // One set of names per open object; null for an open array.
        var scopes = new Stack<HashSet<string>?>();
        try
        {
            if (!reader.Read())
                return ContentVerdict.Invalid("no JSON value", 1, 1);
            do
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        scopes.Push(new HashSet<string>(StringComparer.Ordinal));
                        break;
                    case JsonTokenType.StartArray:
                        scopes.Push(null);
                        break;
                    case JsonTokenType.EndObject or JsonTokenType.EndArray:
                        scopes.Pop();
                        break;
                    case JsonTokenType.PropertyName when duplicates:
                        if (!scopes.Peek()!.Add(reader.GetString()!))
                        {
                            var (line, column) = PositionOf(bytes, reader.TokenStartIndex);
                            return ContentVerdict.Invalid("a duplicate member name in one object", line, column);
                        }
                        break;
                }
            }
            while (reader.Read());
        }
        catch (JsonException e)
        {
            return ContentVerdict.Invalid(e.Message.Contains("depth", StringComparison.OrdinalIgnoreCase)
                    ? $"nesting deeper than {MaxDepth} levels"
                    : "not strict JSON (RFC 8259: no comments, no trailing commas, one value)",
                (e.LineNumber ?? 0) + 1, (e.BytePositionInLine ?? 0) + 1);
        }
        return ContentVerdict.Ok;
    }

    private static (long Line, long Column) PositionOf(byte[] bytes, long offset)
    {
        long line = 1, start = 0;
        for (var i = 0; i < offset; i++)
        {
            if (bytes[i] == (byte)'\n')
            {
                line++;
                start = i + 1;
            }
        }
        return (line, offset - start + 1);
    }
}

/// <summary><c>json-no-duplicate-keys</c>: <see cref="JsonValidator"/>, and no object holds two members with one name.
/// Names are compared unescaped and ordinally (<c>"a"</c> and <c>"a"</c> are one name), at every depth: a
/// keep-both of two sides that added the same member would otherwise give a document whose meaning depends on the
/// parser.</summary>
[Register(typeof(IContentValidator), ServiceLifetime.Singleton)]
public sealed class JsonNoDuplicateKeysValidator : IContentValidator
{
    public string Name => MergeQueueWireRules.JsonNoDuplicateKeys;

    public ContentVerdict Validate(string text) => JsonValidator.Check(text, duplicates: true);
}

/// <summary><c>xml</c>: a well-formed XML document. A DTD is refused (<see cref="DtdProcessing.Prohibit"/>) and no
/// resolver is set, so nothing external is ever read (XXE) and no entity expands.</summary>
[Register(typeof(IContentValidator), ServiceLifetime.Singleton)]
public sealed class XmlValidator : IContentValidator
{
    public string Name => MergeQueueWireRules.Xml;

    public ContentVerdict Validate(string text) => Check(text, msbuildKeys: false);

    /// <summary>Reads the whole document; with <paramref name="msbuildKeys"/>, also refuses a key that one MSBuild group
    /// holds twice (<see cref="MsBuildNoDuplicateKeysValidator"/>).</summary>
    internal static ContentVerdict Check(string text, bool msbuildKeys)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            ConformanceLevel = ConformanceLevel.Document,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            IgnoreProcessingInstructions = true,
        };
        try
        {
            using var reader = XmlReader.Create(new StringReader(text.StartsWith('﻿') ? text[1..] : text), settings);
            // Per open element: the keys of its children when it is an ItemGroup or a PropertyGroup, else null.
            var scopes = new Stack<(HashSet<string>? Keys, bool Items)>();
            while (reader.Read())
            {
                if (!msbuildKeys)
                    continue;
                if (reader.NodeType == XmlNodeType.EndElement)
                {
                    scopes.Pop();
                    continue;
                }
                if (reader.NodeType != XmlNodeType.Element)
                    continue;
                if (scopes.TryPeek(out var group) && group.Keys is not null && !group.Keys.Add(MsBuildKey(reader, group.Items)))
                    return ContentVerdict.Invalid(group.Items ? "an item that one ItemGroup holds twice" : "a property that one PropertyGroup sets twice",
                        ((IXmlLineInfo)reader).LineNumber, ((IXmlLineInfo)reader).LinePosition);
                var name = reader.LocalName;
                var isItems = name.Equals("ItemGroup", StringComparison.OrdinalIgnoreCase);
                var opened = isItems || name.Equals("PropertyGroup", StringComparison.OrdinalIgnoreCase)
                    ? (new HashSet<string>(StringComparer.OrdinalIgnoreCase), isItems)
                    : ((HashSet<string>?)null, false);
                if (!reader.IsEmptyElement)
                    scopes.Push(opened);
            }
        }
        catch (XmlException e)
        {
            return ContentVerdict.Invalid(e.Message.Contains("DTD", StringComparison.OrdinalIgnoreCase)
                    ? "a DTD, which is not allowed"
                    : "not well-formed XML",
                e.LineNumber > 0 ? e.LineNumber : null, e.LinePosition > 0 ? e.LinePosition : null);
        }
        return ContentVerdict.Ok;
    }

    /// <summary>An item's key is its type, the attribute that names its items and that attribute's value, and its
    /// condition; a property's is its name and its condition. MSBuild compares both case-insensitively.</summary>
    private static string MsBuildKey(XmlReader reader, bool item)
    {
        var condition = reader.GetAttribute("Condition")?.Trim() ?? "";
        if (!item)
            return $"{reader.LocalName}\0{condition}";
        foreach (var attribute in (string[])["Include", "Update", "Remove"])
        {
            if (reader.GetAttribute(attribute) is { } value)
                return $"{reader.LocalName}\0{attribute}\0{value.Trim()}\0{condition}";
        }
        return $"{reader.LocalName}\0\0\0{condition}";
    }
}

/// <summary>
/// <c>msbuild-no-duplicate-keys</c> (#570, docs/coverage_backport_PRD.md §8.2): <see cref="XmlValidator"/>, and no
/// <c>ItemGroup</c> or <c>PropertyGroup</c> holds one key twice. A keep-both of two sides that each inserted the same
/// package reference with another version is well-formed XML, yet a broken project (NU1504); two settings of one property
/// in one group are well-formed too, and the last silently wins. Both are conflicts a human decides. An item's key is its
/// type, its <c>Include</c>/<c>Update</c>/<c>Remove</c> value and its <c>Condition</c>; a property's is its name and
/// its <c>Condition</c>, all compared case-insensitively as MSBuild does. Keys are scoped to one group element, so a
/// project that sets a property in two groups (a default, then a conditional override) still passes.
/// </summary>
[Register(typeof(IContentValidator), ServiceLifetime.Singleton)]
public sealed class MsBuildNoDuplicateKeysValidator : IContentValidator
{
    public string Name => MergeQueueWireRules.MsBuildNoDuplicateKeys;

    public ContentVerdict Validate(string text) => XmlValidator.Check(text, msbuildKeys: true);
}

/// <summary><c>no-conflict-markers</c>: no line starts like a conflict marker (<c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c>,
/// <c>|||||||</c>, <c>=======</c>, <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt;</c>). It runs on every content a resolver writes,
/// whatever the rule says (§10.4 step 4).</summary>
[Register(typeof(IContentValidator), ServiceLifetime.Singleton)]
public sealed class NoConflictMarkersValidator : IContentValidator
{
    public string Name => MergeQueueWireRules.NoConflictMarkers;

    public ContentVerdict Validate(string text)
    {
        long line = 0;
        foreach (var raw in ConflictHunks.SplitLines(text))
        {
            line++;
            if (ConflictHunks.LooksLikeMarker(raw.TrimEnd('\r')))
                return ContentVerdict.Invalid("a line that looks like a conflict marker", line, 1);
        }
        return ContentVerdict.Ok;
    }
}
