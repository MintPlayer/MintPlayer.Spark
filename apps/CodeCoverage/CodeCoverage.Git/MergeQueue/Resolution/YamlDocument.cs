using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace CodeCoverage.Git.MergeQueue.Resolution;

/// <summary>A node of a <see cref="YamlDocument"/>: what the event parser read, with its position, anchor, tag and
/// style. Nothing is ever deserialised into a type.</summary>
public abstract class YamlNode
{
    /// <summary>The anchor (<c>&amp;name</c>) on this node, or null.</summary>
    public string? Anchor { get; internal init; }

    /// <summary>An explicit tag (<c>!tag</c>), or null.</summary>
    public string? Tag { get; internal init; }

    /// <summary>1-based line of the node's start.</summary>
    public long Line { get; internal init; }

    public long Column { get; internal init; }

    /// <summary>The number of nodes this one stands for once every alias below it is expanded.</summary>
    internal long ExpandedSize { get; set; } = 1;
}

public sealed class YamlScalar : YamlNode
{
    public required string Value { get; init; }
    public ScalarStyle Style { get; init; }

    /// <summary>A literal (<c>|</c>) or folded (<c>&gt;</c>) block scalar.</summary>
    public bool IsBlock => Style is ScalarStyle.Literal or ScalarStyle.Folded;
}

public sealed class YamlMapping : YamlNode
{
    public bool IsFlow { get; init; }
    public List<KeyValuePair<YamlScalar, YamlNode>> Entries { get; } = [];

    /// <summary>The value under the plain key <paramref name="key"/>, aliases left as they are; null when absent.</summary>
    public YamlNode? this[string key] => Entries.FirstOrDefault(e => e.Key.Value == key).Value;
}

public sealed class YamlSequence : YamlNode
{
    public bool IsFlow { get; init; }
    public List<YamlNode> Items { get; } = [];
}

/// <summary>An alias (<c>*name</c>): kept in the tree so a comparison can tell a node reached through one.</summary>
public sealed class YamlAlias : YamlNode
{
    public required YamlNode Target { get; init; }
}

/// <summary>
/// A YAML stream read with YamlDotNet's event parser into a small tree (docs/code-coverage/dependency-updates-PRD.md R15,
/// gap C). Refused while reading, before any caller sees a node: text that does not parse, a mapping holding one key twice
/// (the meaning of such a document depends on the parser), a key that is not a scalar, a tab in a line's indentation
/// outside a block scalar, more than <see cref="MaxAliases"/> aliases, more than <see cref="MaxExpandedNodes"/> nodes once
/// the aliases are expanded (the "billion laughs" shape) and nesting deeper than <see cref="MaxDepth"/>.
/// </summary>
public sealed class YamlDocument
{
    public const int MaxDepth = 64;
    public const int MaxAliases = 64;
    public const long MaxExpandedNodes = 100_000;

    private YamlDocument(IReadOnlyList<YamlNode> roots) => Roots = roots;

    /// <summary>One root per document of the stream (an empty stream has none).</summary>
    public IReadOnlyList<YamlNode> Roots { get; }

    /// <summary>Resolves an alias to its anchored node; any other node is returned as is.</summary>
    public static YamlNode Resolve(YamlNode node) => node is YamlAlias alias ? alias.Target : node;

    /// <summary>The document, or the verdict that refuses it.</summary>
    public static (YamlDocument? Document, ContentVerdict Verdict) Parse(string text)
    {
        if (text.StartsWith('﻿'))
            text = text[1..];
        var builder = new Builder();
        try
        {
            var parser = new Parser(new StringReader(text));
            while (parser.MoveNext())
            {
                if (builder.Accept(parser.Current!) is { } refused)
                    return (null, refused);
            }
        }
        catch (YamlException e)
        {
            return (null, ContentVerdict.Invalid("not well-formed YAML", e.Start.Line > 0 ? e.Start.Line : null, e.Start.Column > 0 ? e.Start.Column : null));
        }
        if (TabIndentation(text, builder.BlockLines) is { } tab)
            return (null, ContentVerdict.Invalid("a tab in a line's indentation", tab, 1));
        return (new YamlDocument(builder.Roots), ContentVerdict.Ok);
    }

    /// <summary>The first line (1-based) whose indentation holds a tab, outside the body of a block scalar.</summary>
    private static long? TabIndentation(string text, HashSet<long> blockLines)
    {
        long line = 0;
        foreach (var raw in text.Split('\n'))
        {
            line++;
            if (blockLines.Contains(line))
                continue;
            foreach (var c in raw)
            {
                if (c == '\t')
                    return line;
                if (c != ' ')
                    break;
            }
        }
        return null;
    }

    private sealed class Builder
    {
        private readonly Dictionary<string, YamlNode> anchors = new(StringComparer.Ordinal);
        private readonly Stack<Frame> open = new();
        private int aliases;

        public List<YamlNode> Roots { get; } = [];
        public HashSet<long> BlockLines { get; } = [];

        private sealed class Frame(YamlNode node)
        {
            public YamlNode Node { get; } = node;
            public YamlScalar? PendingKey { get; set; }
            public HashSet<string> Keys { get; } = new(StringComparer.Ordinal);
        }

        public ContentVerdict? Accept(ParsingEvent e)
        {
            switch (e)
            {
                case StreamStart or StreamEnd or DocumentStart or DocumentEnd or Comment:
                    return null;
                case Scalar scalar:
                    if (scalar.Style is ScalarStyle.Literal or ScalarStyle.Folded)
                    {
                        for (var l = scalar.Start.Line + 1; l <= scalar.End.Line; l++)
                            BlockLines.Add(l);
                    }
                    return Add(new YamlScalar
                    {
                        Value = scalar.Value,
                        Style = scalar.Style,
                        Anchor = AnchorOf(scalar),
                        Tag = TagOf(scalar),
                        Line = scalar.Start.Line,
                        Column = scalar.Start.Column,
                    }, e);
                case MappingStart start:
                    return Open(new YamlMapping
                    {
                        IsFlow = start.Style == MappingStyle.Flow,
                        Anchor = AnchorOf(start),
                        Tag = TagOf(start),
                        Line = start.Start.Line,
                        Column = start.Start.Column,
                    }, e);
                case SequenceStart start:
                    return Open(new YamlSequence
                    {
                        IsFlow = start.Style == SequenceStyle.Flow,
                        Anchor = AnchorOf(start),
                        Tag = TagOf(start),
                        Line = start.Start.Line,
                        Column = start.Start.Column,
                    }, e);
                case MappingEnd or SequenceEnd:
                    var closed = open.Pop().Node;
                    closed.ExpandedSize = 1 + (closed switch
                    {
                        YamlMapping m => m.Entries.Sum(x => x.Key.ExpandedSize + x.Value.ExpandedSize),
                        YamlSequence s => s.Items.Sum(x => x.ExpandedSize),
                        _ => 0,
                    });
                    if (closed.ExpandedSize > MaxExpandedNodes)
                        return ContentVerdict.Invalid($"more than {MaxExpandedNodes} nodes once its aliases are expanded", closed.Line, closed.Column);
                    if (closed.Anchor is { } anchor)
                        anchors[anchor] = closed;
                    return Place(closed, e);
                case AnchorAlias alias:
                    if (++aliases > MaxAliases)
                        return ContentVerdict.Invalid($"more than {MaxAliases} aliases", alias.Start.Line, alias.Start.Column);
                    if (!anchors.TryGetValue(alias.Value.Value, out var target))
                        return ContentVerdict.Invalid("an alias to an anchor that is not defined before it", alias.Start.Line, alias.Start.Column);
                    return Add(new YamlAlias { Target = target, Line = alias.Start.Line, Column = alias.Start.Column, ExpandedSize = target.ExpandedSize }, e);
                default:
                    return ContentVerdict.Invalid("a YAML construct the validator does not read", e.Start.Line, e.Start.Column);
            }
        }

        private ContentVerdict? Open(YamlNode node, ParsingEvent e)
        {
            if (open.Count >= MaxDepth)
                return ContentVerdict.Invalid($"nesting deeper than {MaxDepth} levels", e.Start.Line, e.Start.Column);
            open.Push(new Frame(node));
            return null;
        }

        private ContentVerdict? Add(YamlNode node, ParsingEvent e)
        {
            if (node is YamlScalar { Anchor: { } anchor })
                anchors[anchor] = node;
            return Place(node, e);
        }

        /// <summary>Puts a finished node into its parent (or the roots), checking keys.</summary>
        private ContentVerdict? Place(YamlNode node, ParsingEvent e)
        {
            if (open.Count == 0)
            {
                Roots.Add(node);
                return Roots.Sum(r => r.ExpandedSize) > MaxExpandedNodes
                    ? ContentVerdict.Invalid($"more than {MaxExpandedNodes} nodes once its aliases are expanded", e.Start.Line, e.Start.Column)
                    : null;
            }
            var frame = open.Peek();
            switch (frame.Node)
            {
                case YamlSequence sequence:
                    sequence.Items.Add(node);
                    return null;
                case YamlMapping mapping when frame.PendingKey is null:
                    if (Resolve(node) is not YamlScalar key)
                        return ContentVerdict.Invalid("a mapping key that is not a scalar", node.Line, node.Column);
                    if (!frame.Keys.Add(key.Value))
                        return ContentVerdict.Invalid("a duplicate key in one mapping", node.Line, node.Column);
                    frame.PendingKey = key;
                    return null;
                case YamlMapping mapping:
                    mapping.Entries.Add(new(frame.PendingKey!, node));
                    frame.PendingKey = null;
                    return null;
                default:
                    return ContentVerdict.Invalid("a YAML construct the validator does not read", e.Start.Line, e.Start.Column);
            }
        }

        private static string? AnchorOf(NodeEvent e) => e.Anchor.IsEmpty ? null : e.Anchor.Value;

        private static string? TagOf(NodeEvent e) => e.Tag.IsEmpty ? null : e.Tag.Value;
    }
}
