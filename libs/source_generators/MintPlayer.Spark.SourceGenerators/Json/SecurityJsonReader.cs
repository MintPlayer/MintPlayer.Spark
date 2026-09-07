using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace MintPlayer.Spark.SourceGenerators.Json;

/// <summary>
/// Extracts the parts of <c>App_Data/security.json</c> an analyzer needs: each right's
/// <c>resource</c> and <c>groupId</c> with the offset of the resource text, and the declared group
/// ids.
/// </summary>
/// <remarks>
/// <para>
/// A minimal reader rather than a JSON parser, following <see cref="ModelJsonReader"/>. That is a
/// deliberate trade and worth stating: the analyzer project is netstandard2.0 with no JSON library,
/// and shipping one into <c>analyzers/dotnet/cs</c> is the classic assembly-load conflict with the
/// compiler's own copy. The shapes needed here are three flat string fields, which does not justify
/// that.
/// </para>
/// <para>
/// What it therefore cannot do, stated so nobody assumes otherwise: it does not validate that the
/// file is well-formed JSON, and it will not see a right written across an unusual layout. Both are
/// acceptable because <b>the runtime loader remains authoritative</b> — a malformed security.json
/// refuses startup. This reader exists to catch the file that loads cleanly and grants nothing, which
/// is the failure no runtime check can see.
/// </para>
/// <para>
/// Content arriving through <c>AdditionalFiles</c> is untrusted, as <see cref="ModelJsonReader"/>
/// records. Nothing read here is emitted as code — only as diagnostic text — so the identifier
/// grammar check that guards the id generator does not apply; values are still length-capped before
/// they reach a message.
/// </para>
/// </remarks>
internal static class SecurityJsonReader
{
    /// <summary>Longest value echoed into a diagnostic, so a pathological file cannot produce a pathological message.</summary>
    private const int MaxEchoedLength = 200;

    internal sealed class RightEntry
    {
        public string Resource { get; set; } = string.Empty;
        public string GroupId { get; set; } = string.Empty;

        /// <summary>Offset of the resource VALUE in the file, for anchoring a squiggle on it.</summary>
        public int ResourceStart { get; set; }

        public int ResourceLength { get; set; }
    }

    // A right object: any order of keys, so both are searched inside one object's braces.
    private static readonly Regex RightObject = new(
        @"\{(?<body>[^{}]*?""(?:resource|groupId)""\s*:[^{}]*?)\}",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ResourceValue = new(
        @"""resource""\s*:\s*""(?<v>(?:[^""\\]|\\.)*)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex GroupIdValue = new(
        @"""groupId""\s*:\s*""(?<v>(?:[^""\\]|\\.)*)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex GroupsKey = new(
        @"""groups""\s*:\s*\{", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex GuidKey = new(
        @"""(?<v>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})""\s*:",
        RegexOptions.Compiled);

    /// <summary>Every right the file declares, in file order.</summary>
    public static IReadOnlyList<RightEntry> ReadRights(string text)
    {
        var results = new List<RightEntry>();
        if (string.IsNullOrEmpty(text)) return results;

        foreach (Match obj in RightObject.Matches(text))
        {
            var body = obj.Groups["body"];
            var resource = ResourceValue.Match(body.Value);
            if (!resource.Success) continue;

            var value = resource.Groups["v"];
            var group = GroupIdValue.Match(body.Value);

            results.Add(new RightEntry
            {
                Resource = Truncate(value.Value),
                GroupId = group.Success ? group.Groups["v"].Value : string.Empty,
                ResourceStart = body.Index + value.Index,
                ResourceLength = value.Length,
            });
        }

        return results;
    }

    /// <summary>The group ids the file declares, for spotting a right that names one it does not.</summary>
    public static ISet<string> ReadGroupIds(string text)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(text)) return ids;

        var groups = GroupsKey.Match(text);
        if (!groups.Success) return ids;

        // Brace-matched rather than regex-terminated. A group's value is a translated string — an
        // object — so the block contains nested braces, and a lazy ".*?\n\s*\}" stops at the first
        // nested close. That reported perfectly well-declared groups as dangling, on a real file, in
        // the first run against the demo apps.
        var body = ExtractBracedBlock(text, groups.Index + groups.Length - 1);
        if (body is null) return ids;

        foreach (Match key in GuidKey.Matches(body))
            ids.Add(key.Groups["v"].Value);

        return ids;
    }

    /// <summary>
    /// The text between the brace at <paramref name="openIndex"/> and its match, counting depth and
    /// ignoring braces inside strings.
    /// </summary>
    private static string? ExtractBracedBlock(string text, int openIndex)
    {
        if (openIndex < 0 || openIndex >= text.Length || text[openIndex] != '{') return null;

        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = openIndex; i < text.Length; i++)
        {
            var c = text[i];

            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return text.Substring(openIndex + 1, i - openIndex - 1);
        }

        return null;
    }

    private static string Truncate(string value)
        => value.Length <= MaxEchoedLength ? value : value.Substring(0, MaxEchoedLength) + "…";
}
