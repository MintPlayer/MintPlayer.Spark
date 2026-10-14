using System.Text;
using System.Text.Json;

namespace MintPlayer.Spark.Services;

/// <summary>
/// The canonical order of <c>security.json</c>'s <c>rights[]</c> (<c>docs/coverage_merge_queue_PRD.md</c> O5):
/// by the resource's target (<c>Person</c> in <c>QueryReadEditNewDelete/Person</c>), then <c>groupId</c>,
/// then the right part (<c>QueryReadEditNewDelete</c>), then <c>key</c>. All ordinal.
/// </summary>
/// <remarks>
/// <para>
/// Why: rights used to be appended, so any two pull requests that granted something conflicted on the
/// array's last line. In this order two PRs for different entities insert in different places, and git
/// merges them.
/// </para>
/// <para>
/// The file is hand-authored, so this is a text operation, not a serializer round trip: each right's own
/// text moves as one block, whether it is one line with column padding or an indented object over several
/// lines, and every byte outside the array's elements stays where it was. When the order is already
/// right the text is returned unchanged. When it is not, the rights are joined by the separator the array
/// uses most often, so a blank line that grouped rights under the old order does not land between two
/// unrelated rights.
/// </para>
/// </remarks>
internal static class SecurityRightsOrder
{
    private const string RightsMember = "rights";

    /// <summary>Whether <paramref name="json"/>'s rights are in the canonical order (or there are none).</summary>
    /// <exception cref="InvalidOperationException">The file is not strict JSON, or <c>rights</c> is not an array.</exception>
    internal static bool IsSorted(string json) => ReferenceEquals(Sort(json), json);

    /// <summary>
    /// <paramref name="json"/> with its rights in the canonical order; the same instance when they already are.
    /// </summary>
    /// <exception cref="InvalidOperationException">The file is not strict JSON, or <c>rights</c> is not an array.</exception>
    internal static string Sort(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        var bom = json.Length > 0 && json[0] == '﻿' ? "﻿" : "";
        var bytes = Encoding.UTF8.GetBytes(json[bom.Length..]);

        List<(int Start, int End)> elements;
        try
        {
            elements = Elements(bytes);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"The rights cannot be put in order: the file is not valid JSON ({ex.Message}).", ex);
        }

        if (elements.Count < 2)
            return json;

        var keyed = elements
            .Select((e, index) => (Element: e, Index: index, Key: SortKey(bytes.AsSpan(e.Start, e.End - e.Start))))
            .ToList();
        var sorted = keyed
            .OrderBy(x => x.Key.Target, StringComparer.Ordinal)
            .ThenBy(x => x.Key.GroupId, StringComparer.Ordinal)
            .ThenBy(x => x.Key.Right, StringComparer.Ordinal)
            .ThenBy(x => x.Key.Key, StringComparer.Ordinal)
            .ToList();
        if (sorted.Select(x => x.Index).SequenceEqual(keyed.Select(x => x.Index)))
            return json;

        // The separators are ',' plus whitespace: strict JSON allows nothing else between two elements.
        var separator = Enumerable.Range(0, elements.Count - 1)
            .Select(i => Encoding.UTF8.GetString(bytes, elements[i].End, elements[i + 1].Start - elements[i].End))
            .GroupBy(s => s, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .First().Key;

        var builder = new StringBuilder(json.Length + 16);
        builder.Append(bom);
        builder.Append(Encoding.UTF8.GetString(bytes, 0, elements[0].Start));
        for (var i = 0; i < sorted.Count; i++)
        {
            if (i > 0) builder.Append(separator);
            var (start, end) = sorted[i].Element;
            builder.Append(Encoding.UTF8.GetString(bytes, start, end - start));
        }
        builder.Append(Encoding.UTF8.GetString(bytes, elements[^1].End, bytes.Length - elements[^1].End));
        return builder.ToString();
    }

    /// <summary>The byte ranges of the top-level <c>rights</c> array's elements, in file order.</summary>
    private static List<(int Start, int End)> Elements(byte[] bytes)
    {
        var elements = new List<(int, int)>();
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow });

        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw new InvalidOperationException("The rights cannot be put in order: the file is not a JSON object.");

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var isRights = reader.ValueTextEquals(RightsMember);
            reader.Read();
            if (!isRights)
            {
                reader.Skip();
                continue;
            }

            if (reader.TokenType != JsonTokenType.StartArray)
                throw new InvalidOperationException("The rights cannot be put in order: \"rights\" is not an array.");

            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                var start = (int)reader.TokenStartIndex;
                reader.Skip();
                elements.Add((start, (int)reader.BytesConsumed));
            }
        }

        // Read to the end, so text after the array that is not JSON is reported rather than kept.
        while (reader.Read()) { }
        return elements;
    }

    private static (string Target, string GroupId, string Right, string Key) SortKey(ReadOnlySpan<byte> element)
    {
        var reader = new Utf8JsonReader(element);
        string? resource = null, groupId = null, key = null;
        if (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
        {
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString();
                reader.Read();
                var value = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                reader.Skip();
                switch (name)
                {
                    case "resource": resource = value; break;
                    case "groupId": groupId = value; break;
                    case "key": key = value; break;
                }
            }
        }

        resource ??= "";
        var slash = resource.IndexOf('/');
        var target = slash < 0 ? resource : resource[(slash + 1)..];
        var right = slash < 0 ? "" : resource[..slash];
        return (target, groupId ?? "", right, key ?? "");
    }
}
