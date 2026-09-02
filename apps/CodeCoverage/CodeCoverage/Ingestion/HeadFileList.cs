using System.Text.RegularExpressions;

namespace CodeCoverage.Ingestion;

/// <summary>
/// The uploader's view of the repository tree at the measured commit. Two wire
/// formats share one form field: v1 is <c>git ls-files</c> (one path per line),
/// v2 is <c>git ls-files -s</c> reduced to <c>&lt;blob-oid&gt; &lt;path&gt;</c> per
/// line. The format is sniffed from the first non-empty line so old action
/// builds keep working; v1 simply yields no OIDs, and without OIDs nothing is
/// ever carried forward into an assembly.
/// </summary>
public sealed partial class HeadFileList
{
    public static readonly HeadFileList Empty = new([], [], hasOids: false);

    private readonly Dictionary<string, string?> pathToOid;

    private HeadFileList(IReadOnlyList<string> paths, Dictionary<string, string?> pathToOid, bool hasOids)
    {
        Paths = paths;
        this.pathToOid = pathToOid;
        HasOids = hasOids;
    }

    /// <summary>Repo-relative forward-slash paths in upload order.</summary>
    public IReadOnlyList<string> Paths { get; }

    /// <summary>True when the upload used the v2 format and OIDs are available.</summary>
    public bool HasOids { get; }

    public int Count => Paths.Count;

    /// <summary>The blob OID of a (unified) path, or null when unknown or absent.</summary>
    public string? OidFor(string unifiedPath)
        => pathToOid.TryGetValue(unifiedPath, out var oid) ? oid : null;

    public static HeadFileList Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return Empty;

        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
            return Empty;

        var v2 = OidPrefix().IsMatch(lines[0]);
        var paths = new List<string>(lines.Length);
        var map = new Dictionary<string, string?>(lines.Length, StringComparer.Ordinal);

        foreach (var line in lines)
        {
            string path;
            string? oid = null;
            if (v2)
            {
                var match = OidPrefix().Match(line);
                if (!match.Success)
                    continue; // a stray line in a v2 payload carries no usable path
                oid = match.Groups["oid"].Value.ToLowerInvariant();
                path = line[match.Length..];
            }
            else
            {
                path = line;
            }

            path = Unify(path);
            if (path.Length == 0)
                continue;

            if (map.TryAdd(path, oid))
                paths.Add(path);
        }

        return new HeadFileList(paths, map, hasOids: v2);
    }

    /// <summary>Same unification <see cref="PathNormalizer"/> applies: backslashes become slashes.</summary>
    public static string Unify(string path) => path.Replace('\\', '/');

    [GeneratedRegex("^(?<oid>[0-9a-fA-F]{40}|[0-9a-fA-F]{64}) +")]
    private static partial Regex OidPrefix();
}
