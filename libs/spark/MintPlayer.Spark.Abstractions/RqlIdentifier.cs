namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Validates a collection name or field path before it is spliced into RQL or a patch script.
/// </summary>
/// <remarks>
/// A <em>value</em> — an id, a prefix, a string to compare against — never belongs in query text: pass it
/// as a query parameter (<c>$p</c> with <c>AddParameter</c>, <c>IndexQuery.QueryParameters</c>, or a
/// patch's <c>Values</c>). An <em>identifier</em> cannot be a parameter in RQL, so it has to be spliced,
/// and this is the gate it passes first.
/// <para>
/// ⚠️ An allow-list, not an escape. RavenDB's own escaping (<c>QueryFieldUtil</c>) is internal to the
/// client, and an escape that is wrong leaks silently — an earlier queue-name escaper here missed the
/// backslash and still closed the literal. Every name this is used with comes from a constant table, a
/// CLR property or the collection-name convention, so a strict shape costs nothing and anything outside
/// it is refused with an <see cref="ArgumentException"/> before a query is built.
/// </para>
/// <para>
/// Public because migrations in applications build patch-by-query scripts too (#264): the framework, its
/// authorization package and the applications all splice the same two kinds of identifier.
/// </para>
/// </remarks>
public static class RqlIdentifier
{
    /// <summary>
    /// Returns <paramref name="name"/> unchanged when it is a plain collection name — ASCII letters,
    /// digits and underscores, starting with a letter — and throws otherwise.
    /// </summary>
    /// <exception cref="ArgumentException">The name is empty or has any other character.</exception>
    public static string Collection(string name)
    {
        if (!IsSegment(name.AsSpan()))
            throw new ArgumentException(
                $"'{name}' is not a collection name that may be spliced into RQL: only ASCII letters, digits and "
                + "underscores are allowed, starting with a letter.", nameof(name));
        return name;
    }

    /// <summary>
    /// Returns <paramref name="path"/> unchanged when it is a dot-separated field path whose every segment
    /// is shaped like a collection name, optionally followed by <c>[]</c> to step into an array
    /// (<c>Origin.FromBuildId</c>, <c>Jobs[].Id</c>), and throws otherwise.
    /// </summary>
    /// <exception cref="ArgumentException">The path is empty or has an empty or malformed segment.</exception>
    public static string FieldPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            throw Invalid(path);

        foreach (var range in path.AsSpan().Split('.'))
        {
            var segment = path.AsSpan()[range];
            if (segment.EndsWith("[]"))
                segment = segment[..^2];
            if (!IsSegment(segment))
                throw Invalid(path);
        }
        return path;

        static ArgumentException Invalid(string path) => new(
            $"'{path}' is not a field path that may be spliced into RQL: each dot-separated segment must be ASCII "
            + "letters, digits and underscores starting with a letter, optionally followed by '[]'.", nameof(path));
    }

    private static bool IsSegment(ReadOnlySpan<char> segment)
    {
        if (segment.IsEmpty || !char.IsAsciiLetter(segment[0]))
            return false;

        foreach (var c in segment)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
                return false;
        }
        return true;
    }
}
