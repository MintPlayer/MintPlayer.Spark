namespace CodeCoverage.Git.MergeQueue.Resolution.Manifests;

/// <summary>
/// One ecosystem's manifest files as keyed-merge reads them (#570 K2, docs/coverage_backport_plan.md). Every format parses
/// its lines into the one structure the merge works on (<see cref="ManifestSection"/> of <see cref="ManifestEntry"/>),
/// says which values are versions and orders them, and writes values back into an entry's own text. The three-way merge
/// per key is <see cref="KeyedMergeResolver"/>'s and does not change per format.
/// </summary>
/// <remarks>
/// Formats are <c>[Register]</c>ed services, injected into the resolver as a list. A new one is added for a repository
/// that needs it, with tests on its real files (K3). Lockfiles are never a format: they stay take-base + regenerate, and
/// the regenerate run rebuilds them from the merged manifest.
/// </remarks>
public interface IManifestFormat
{
    /// <summary>A unique name, for messages and tests (<c>msbuild</c>, <c>npm</c>, <c>composer</c>).</summary>
    string Name { get; }

    /// <summary>Whether this format reads the file named <paramref name="fileName"/> (no directory). At most one format
    /// reads a name.</summary>
    bool Handles(string fileName);

    /// <summary>How many lines of the common text just above and just below a conflict region belong to the same entry,
    /// so the region is widened to whole entries (an MSBuild item whose child <c>&lt;Version&gt;</c> line alone
    /// conflicts). <c>(0, 0)</c> when the region already starts and ends on entry boundaries.</summary>
    (int Above, int Below) Enclose(IReadOnlyList<string> before, IReadOnlyList<string> after);

    /// <summary>A region's section (its lines, each with its terminator) as entries; null when a line is not an
    /// entry, which refuses the path.</summary>
    ManifestSection? Read(IReadOnlyList<string> lines);

    /// <summary>The order of two values of <paramref name="valueName"/> on <paramref name="entry"/>: negative, zero or
    /// positive as <paramref name="a"/> is lower, equal or higher. Null when the value is no version (a property, a
    /// package's own version, a platform requirement) or either value has no order (a float, a range, a variable).
    /// <paramref name="before"/> is the merged text above the region, for formats whose meaning depends on the enclosing
    /// object.</summary>
    int? CompareVersions(ManifestEntry entry, string valueName, string a, string b, IReadOnlyList<string> before);

    /// <summary><paramref name="entry"/>'s lines with each named value set (null removes it) and every other byte kept;
    /// null when a value can't be placed safely.</summary>
    IReadOnlyList<string>? Write(ManifestEntry entry, IReadOnlyDictionary<string, string?> values);

    /// <summary>The merged region's lines: each entry's leading and own lines in order, then <paramref name="trailing"/>,
    /// with what the format's syntax needs between entries (a JSON object's commas).</summary>
    IReadOnlyList<string> Join(IReadOnlyList<ManifestEntry> merged, IReadOnlyList<string> trailing, ManifestSection ours, ManifestSection theirs);
}

/// <summary>
/// One keyed entry: the lines before it that travel with it (comments, blank lines), its own lines, its key (compared
/// ordinally; a format folds case into it where its ecosystem does), its kind (an MSBuild item type), and its values by
/// name. A <see cref="Scalar"/> entry has one value, named <see cref="ScalarValue"/>; a collection entry (an MSBuild
/// item) has one per metadata name.
/// </summary>
public sealed record ManifestEntry(IReadOnlyList<string> Leading, IReadOnlyList<string> Lines, string Key, string Kind, bool Scalar,
    IReadOnlyDictionary<string, string> Values)
{
    /// <summary>The name of a scalar entry's one value.</summary>
    public const string ScalarValue = "";
}

/// <summary>A region section's entries, and the comment or blank lines after the last one.</summary>
public sealed record ManifestSection(IReadOnlyList<ManifestEntry> Entries, IReadOnlyList<string> Trailing);

/// <summary>The built-in formats, for code that runs the resolver without a container; a test holds them equal to the
/// <c>[Register]</c>ed set.</summary>
public static class ManifestFormats
{
    public static IReadOnlyList<IManifestFormat> BuiltIn { get; } = [new MsBuildManifestFormat(), new NpmManifestFormat(), new ComposerManifestFormat()];

    /// <summary>The single format that reads <paramref name="path"/>'s file name, or null.</summary>
    public static IManifestFormat? For(IEnumerable<IManifestFormat> formats, string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        return formats.SingleOrDefault(f => f.Handles(name));
    }

    /// <summary>Plain concatenation, the <see cref="IManifestFormat.Join"/> of a format with no separators.</summary>
    public static IReadOnlyList<string> Concatenate(IReadOnlyList<ManifestEntry> merged, IReadOnlyList<string> trailing)
    {
        var lines = new List<string>();
        foreach (var entry in merged)
        {
            lines.AddRange(entry.Leading);
            lines.AddRange(entry.Lines);
        }
        lines.AddRange(trailing);
        return lines;
    }
}
