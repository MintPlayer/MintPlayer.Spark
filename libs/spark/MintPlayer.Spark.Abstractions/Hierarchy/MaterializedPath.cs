namespace MintPlayer.Spark.Abstractions.Hierarchy;

/// <summary>
/// Helpers for a <b>materialized path</b>: a tree node stores the whole chain of its ancestors as one
/// string, <c>"site/zone/aisle/rack/level/bin"</c>, so "everything under this node" is a single prefix
/// comparison instead of a recursive walk (warehouse PRD §4.5 and WX9).
/// </summary>
/// <remarks>
/// <b>Convention.</b> Segments are joined by <see cref="Separator"/> (<c>/</c>); a root's path is its own
/// segment. A segment is non-empty, has no separator and no leading or trailing white space; every
/// method that builds a path refuses anything else with an <see cref="ArgumentException"/>, because a
/// stray <c>/</c> inside a segment silently invents a level that no document represents.
/// <para>
/// <b>Descendants by prefix.</b> A node's descendants are the paths starting with
/// <see cref="DescendantPrefix"/> (the path plus the separator), never with the bare path: <c>A/1</c> is
/// not a descendant of <c>A/10</c>, but <c>"A/10".StartsWith("A/1")</c> is true. The RavenDB side
/// (<c>MintPlayer.Spark.Hierarchy.MaterializedPathQueries</c>) builds its prefix query from this, and
/// passes the prefix as a query parameter.
/// </para>
/// <para>
/// The path is a denormalized copy of the parent chain. The parent reference (what the hierarchical
/// reference picker filters on) stays the source of truth for "who is my parent"; the path is what makes
/// subtree reads cheap. Re-parenting rewrites both: see <see cref="Reparent"/>.
/// </para>
/// </remarks>
public static class MaterializedPath
{
    /// <summary>The character between two segments.</summary>
    public const char Separator = '/';

    /// <summary>The path of a root node: its segment, validated.</summary>
    /// <exception cref="ArgumentException">The segment is empty, padded with white space, or contains the separator.</exception>
    public static string Root(string segment) => ValidateSegment(segment);

    /// <summary>
    /// The path of a child of <paramref name="parentPath"/> named <paramref name="segment"/>; a root's path
    /// when <paramref name="parentPath"/> is <see langword="null"/> or empty.
    /// </summary>
    /// <exception cref="ArgumentException">The parent path or the segment is malformed.</exception>
    public static string Child(string? parentPath, string segment)
    {
        ValidateSegment(segment);
        if (string.IsNullOrEmpty(parentPath))
            return segment;
        Validate(parentPath);
        return string.Concat(parentPath, Separator.ToString(), segment);
    }

    /// <summary>The path built from <paramref name="segments"/>, root first.</summary>
    /// <exception cref="ArgumentException">There are no segments, or one is malformed.</exception>
    public static string Combine(IEnumerable<string> segments)
    {
        string? path = null;
        foreach (var segment in segments)
            path = Child(path, segment);
        return path ?? throw new ArgumentException("A path needs at least one segment.", nameof(segments));
    }

    /// <summary>The segments of <paramref name="path"/>, root first.</summary>
    /// <exception cref="ArgumentException">The path is malformed.</exception>
    public static string[] Segments(string path)
    {
        Validate(path);
        return path.Split(Separator);
    }

    /// <summary>The number of segments: <c>1</c> for a root.</summary>
    /// <exception cref="ArgumentException">The path is malformed.</exception>
    public static int Depth(string path)
    {
        Validate(path);
        return path.AsSpan().Count(Separator) + 1;
    }

    /// <summary>The last segment: the node's own name.</summary>
    /// <exception cref="ArgumentException">The path is malformed.</exception>
    public static string Leaf(string path)
    {
        Validate(path);
        var cut = path.LastIndexOf(Separator);
        return cut < 0 ? path : path[(cut + 1)..];
    }

    /// <summary>The parent's path, or <see langword="null"/> for a root.</summary>
    /// <exception cref="ArgumentException">The path is malformed.</exception>
    public static string? Parent(string path)
    {
        Validate(path);
        var cut = path.LastIndexOf(Separator);
        return cut < 0 ? null : path[..cut];
    }

    /// <summary>
    /// The paths of every proper ancestor, root first: <c>"a/b/c"</c> gives <c>["a", "a/b"]</c>. Empty for a
    /// root. What a breadcrumb or an "expand to this node" needs, without a load per level.
    /// </summary>
    /// <exception cref="ArgumentException">The path is malformed.</exception>
    public static IReadOnlyList<string> Ancestors(string path)
    {
        Validate(path);
        var result = new List<string>();
        for (var i = path.IndexOf(Separator); i >= 0; i = path.IndexOf(Separator, i + 1))
            result.Add(path[..i]);
        return result;
    }

    /// <summary>
    /// The prefix every descendant's path starts with: the path plus the separator. Never compare against the
    /// bare path (see the remarks on <see cref="MaterializedPath"/>).
    /// </summary>
    /// <exception cref="ArgumentException">The path is malformed.</exception>
    public static string DescendantPrefix(string path)
    {
        Validate(path);
        return path + Separator;
    }

    /// <summary>Whether <paramref name="path"/> lies strictly below <paramref name="ancestorPath"/>.</summary>
    /// <exception cref="ArgumentException">Either path is malformed.</exception>
    public static bool IsDescendantOf(string path, string ancestorPath)
    {
        Validate(path);
        return path.StartsWith(DescendantPrefix(ancestorPath), StringComparison.Ordinal);
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="ancestorPath"/> itself or lies below it.</summary>
    /// <exception cref="ArgumentException">Either path is malformed.</exception>
    public static bool IsSelfOrDescendantOf(string path, string ancestorPath)
    {
        Validate(path);
        Validate(ancestorPath);
        return string.Equals(path, ancestorPath, StringComparison.Ordinal) || IsDescendantOf(path, ancestorPath);
    }

    /// <summary>
    /// The new path of <paramref name="path"/> when the subtree rooted at <paramref name="movedRootPath"/> is
    /// moved under <paramref name="newParentPath"/> (<see langword="null"/>: it becomes a root). The moved
    /// root keeps its own segment; everything below it keeps its relative path.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// A path is malformed, <paramref name="path"/> is not in the moved subtree, or the new parent lies inside
    /// the moved subtree (a node cannot become its own descendant: the tree would close into a cycle).
    /// </exception>
    public static string Reparent(string path, string movedRootPath, string? newParentPath)
    {
        Validate(path);
        Validate(movedRootPath);
        if (!IsSelfOrDescendantOf(path, movedRootPath))
            throw new ArgumentException($"'{path}' is not in the subtree rooted at '{movedRootPath}'.", nameof(path));

        var newRootPath = ReparentRoot(movedRootPath, newParentPath);
        return newRootPath + path[movedRootPath.Length..];
    }

    /// <summary>
    /// The moved root's new path: <paramref name="newParentPath"/> plus the root's own segment.
    /// </summary>
    /// <exception cref="ArgumentException">A path is malformed, or the new parent lies inside the moved subtree.</exception>
    public static string ReparentRoot(string movedRootPath, string? newParentPath)
    {
        Validate(movedRootPath);
        if (newParentPath is not null && IsSelfOrDescendantOf(newParentPath, movedRootPath))
            throw new ArgumentException(
                $"'{movedRootPath}' cannot move under '{newParentPath}': that is inside its own subtree.", nameof(newParentPath));
        return Child(newParentPath, Leaf(movedRootPath));
    }

    /// <summary>Whether <paramref name="path"/> is well formed: one or more valid segments joined by the separator.</summary>
    public static bool IsValid(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        foreach (var range in path.AsSpan().Split(Separator))
            if (!IsValidSegment(path.AsSpan()[range]))
                return false;
        return true;
    }

    /// <summary>Returns <paramref name="path"/> when it is well formed, and throws otherwise.</summary>
    /// <exception cref="ArgumentException">The path is empty or has an empty, padded segment.</exception>
    public static string Validate(string path)
    {
        if (!IsValid(path))
            throw new ArgumentException(
                $"'{path}' is not a materialized path: segments must be non-empty, without '{Separator}' and without leading or trailing white space.",
                nameof(path));
        return path;
    }

    private static string ValidateSegment(string segment)
    {
        if (segment is null || !IsValidSegment(segment.AsSpan()) || segment.Contains(Separator))
            throw new ArgumentException(
                $"'{segment}' is not a path segment: it must be non-empty, without '{Separator}' and without leading or trailing white space.",
                nameof(segment));
        return segment;
    }

    private static bool IsValidSegment(ReadOnlySpan<char> segment)
        => !segment.IsEmpty
           && !char.IsWhiteSpace(segment[0])
           && !char.IsWhiteSpace(segment[^1]);
}
