namespace CodeCoverage.Versioning.Releases;

/// <summary>
/// #693 D6, M9: what a package's public API gained and lost since its previous release, for the notes' Public API section.
/// The action computes it after the build, from the normalized listing (#153 D6) of this build and of the previous
/// version's registry package (S8), so both hosts get it through the manifest. Removals are breaking.
/// </summary>
public sealed record ApiDelta
{
	/// <summary>The most lines one delta carries; the rest is counted in <see cref="Omitted"/>.</summary>
	public const int MaxLines = 100;
	public const int MaxLineLength = 400;

	public IReadOnlyList<string> Added { get; init; } = [];

	public IReadOnlyList<string> Removed { get; init; } = [];

	/// <summary>How many lines past <see cref="MaxLines"/> were left out.</summary>
	public int Omitted { get; init; }

	/// <summary>
	/// The delta from <paramref name="previous"/> to <paramref name="current"/>, or null when they list the same API.
	/// A .NET listing prefixes each line with its target framework: a member that changed in every framework is one line,
	/// one that changed in only some names them, and a framework added or dropped is one line instead of all its members.
	/// </summary>
	public static ApiDelta? Between(IReadOnlyList<string> previous, IReadOnlyList<string> current, bool prefixedWithTargetFramework)
	{
		List<string> added, removed;
		if (prefixedWithTargetFramework)
		{
			var before = previous.Select(Split).ToList();
			var after = current.Select(Split).ToList();
			var tfmsBefore = before.Select(l => l.Tfm).ToHashSet(StringComparer.Ordinal);
			var tfmsAfter = after.Select(l => l.Tfm).ToHashSet(StringComparer.Ordinal);
			added = [.. tfmsAfter.Except(tfmsBefore).Order(StringComparer.Ordinal).Select(t => $"target framework {t}"),
				.. Changed(after, before, tfmsBefore, tfmsAfter)];
			removed = [.. tfmsBefore.Except(tfmsAfter).Order(StringComparer.Ordinal).Select(t => $"target framework {t}"),
				.. Changed(before, after, tfmsAfter, tfmsBefore)];
		}
		else
		{
			added = [.. current.Except(previous, StringComparer.Ordinal).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
			removed = [.. previous.Except(current, StringComparer.Ordinal).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
		}
		if (added.Count == 0 && removed.Count == 0) return null;

		// Removals are the breaking half, so they are kept first.
		var keptRemoved = removed.Take(MaxLines).Select(Cut).ToList();
		var keptAdded = added.Take(MaxLines - keptRemoved.Count).Select(Cut).ToList();
		return new ApiDelta { Added = keptAdded, Removed = keptRemoved, Omitted = added.Count + removed.Count - keptAdded.Count - keptRemoved.Count };
	}

	/// <summary>
	/// The members in <paramref name="from"/> and not in <paramref name="to"/>, within the frameworks both sides build
	/// (<paramref name="otherTfms"/>); a whole framework on one side only is reported once by the caller.
	/// </summary>
	private static IEnumerable<string> Changed(List<(string Tfm, string Member)> from, List<(string Tfm, string Member)> to, HashSet<string> otherTfms, HashSet<string> ownTfms)
	{
		var shared = ownTfms.Where(otherTfms.Contains).ToHashSet(StringComparer.Ordinal);
		var other = to.ToHashSet();
		return from.Where(l => shared.Contains(l.Tfm) && !other.Contains(l))
			.GroupBy(l => l.Member, StringComparer.Ordinal)
			.OrderBy(g => g.Key, StringComparer.Ordinal)
			.Select(g =>
			{
				var tfms = g.Select(l => l.Tfm).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
				return tfms.Count == shared.Count ? g.Key : $"{g.Key} ({string.Join(", ", tfms)})";
			});
	}

	/// <summary>What the delta's lines weigh against <see cref="ReleaseManifest.MaxApiCharacters"/>.</summary>
	[System.Text.Json.Serialization.JsonIgnore]
	public long Characters => (Added?.Sum(l => (long)(l?.Length ?? 0)) ?? 0) + (Removed?.Sum(l => (long)(l?.Length ?? 0)) ?? 0);

	private static (string Tfm, string Member) Split(string line)
	{
		var space = line.IndexOf(' ');
		return space < 0 ? ("", line) : (line[..space], line[(space + 1)..]);
	}

	private static string Cut(string line) => line.Length <= MaxLineLength ? line : line[..(MaxLineLength - 1)] + "…";

	internal string? Validate()
	{
		if (Added is null || Removed is null || Added.Count + Removed.Count > MaxLines) return $"api holds at most {MaxLines} lines.";
		if (Added.Concat(Removed).Any(l => l is null || l.Length is 0 or > MaxLineLength || l.Any(char.IsControl)))
			return $"api lines are single lines of at most {MaxLineLength} characters.";
		if (Omitted is < 0 or > 1_000_000) return "api.omitted is out of range.";
		return null;
	}
}
