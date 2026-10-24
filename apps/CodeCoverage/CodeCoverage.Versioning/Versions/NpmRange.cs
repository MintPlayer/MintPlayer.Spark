using System.Globalization;
using System.Text.RegularExpressions;

namespace CodeCoverage.Versioning.Versions;

/// <summary>
/// An npm semver range (<c>^22.0.0 || ^23.0.0-next.0</c>, <c>~1.2</c>, <c>&gt;=1.0.0 &lt;2</c>, <c>1.x</c>,
/// <c>1.2.3 - 2</c>, <c>*</c>), with npm's own prerelease rule: a prerelease version is admitted only by a comparator set
/// that names a prerelease of the same <c>X.Y.Z</c>. Used for the peer-range check of an anchor (PRD D7) and for
/// whether a dependent's range still admits a new release (D4). Protocols (<c>workspace:</c>, <c>file:</c>,
/// <c>npm:</c>), tags and urls are not ranges: <see cref="TryParse"/> refuses them and the caller decides.
/// </summary>
public sealed partial class NpmRange
{
	private enum Op { Lt, Le, Gt, Ge, Eq }

	private readonly record struct Comparator(Op Op, SemVer Version)
	{
		public bool Test(SemVer v)
		{
			var c = v.CompareTo(Version);
			return Op switch
			{
				Op.Lt => c < 0,
				Op.Le => c <= 0,
				Op.Gt => c > 0,
				Op.Ge => c >= 0,
				_ => c == 0,
			};
		}
	}

	private readonly List<List<Comparator>> sets;

	private NpmRange(List<List<Comparator>> sets) => this.sets = sets;

	public string Text { get; private init; } = "";

	[GeneratedRegex(@"([<>]=?|=|~>?|\^)\s+", RegexOptions.CultureInvariant)]
	private static partial Regex OperatorSpace();

	[GeneratedRegex(@"^(<=|>=|<|>|=|~>?|\^)?v?([0-9]+|[xX*])(?:\.([0-9]+|[xX*]))?(?:\.([0-9]+|[xX*]))?(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z.-]+)?\z", RegexOptions.CultureInvariant)]
	private static partial Regex Primitive();

	public static bool TryParse(string? text, out NpmRange range)
	{
		range = null!;
		if (text is null || text.Length > 256) return false;
		var sets = new List<List<Comparator>>();
		foreach (var alternative in text.Split("||"))
		{
			var set = new List<Comparator>();
			var trimmed = OperatorSpace().Replace(alternative.Trim(), "$1");
			var hyphen = trimmed.Split(" - ");
			if (hyphen.Length == 2)
			{
				if (!Partial.TryParse(hyphen[0].Trim(), out var from) || !Partial.TryParse(hyphen[1].Trim(), out var to)) return false;
				if (from.Major is not null) set.Add(new(Op.Ge, from.Floor()));
				if (to.Major is not null) set.Add(to.IsFull ? new(Op.Le, to.Floor()) : new(Op.Lt, to.Ceiling()));
			}
			else if (hyphen.Length > 2) return false;
			else
			{
				foreach (var token in trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries))
					if (!Desugar(token, set)) return false;
			}
			sets.Add(set);
		}
		range = new NpmRange(sets) { Text = text };
		return true;
	}

	public bool Admits(SemVer version)
	{
		foreach (var set in sets)
		{
			if (!set.All(c => c.Test(version))) continue;
			if (version.Prerelease is null || set.Any(c => c.Version.Prerelease is not null && c.Version.SameCore(version))) return true;
		}
		return false;
	}

	public bool Admits(string version) => SemVer.TryParse(version, out var v) && Admits(v);

	private static bool Desugar(string token, List<Comparator> set)
	{
		var match = Primitive().Match(token);
		if (!match.Success) return false;
		var op = match.Groups[1].Value;
		var p = new Partial(Part(match.Groups[2]), Part(match.Groups[3]), Part(match.Groups[4]), match.Groups[5].Success ? match.Groups[5].Value : null);
		if (!p.IsWellFormed) return false;

		switch (op)
		{
			case "" or "=":
				if (p.IsFull) set.Add(new(Op.Eq, p.Floor()));
				else if (p.Major is not null) { set.Add(new(Op.Ge, p.Floor())); set.Add(new(Op.Lt, p.Ceiling())); }
				return true; // "*": any stable version, so no comparator
			case "~" or "~>":
				if (p.Major is null) return true;
				set.Add(new(Op.Ge, p.Floor()));
				set.Add(new(Op.Lt, p.Minor is null ? Bound(p.Major.Value + 1, 0, 0) : Bound(p.Major.Value, p.Minor.Value + 1, 0)));
				return true;
			case "^":
				if (p.Major is null) return true;
				set.Add(new(Op.Ge, p.Floor()));
				set.Add(new(Op.Lt, (p.Major, p.Minor, p.Patch) switch
				{
					(> 0, _, _) or (_, null, _) => Bound(p.Major.Value + 1, 0, 0),
					(0, > 0, _) or (0, _, null) => Bound(0, p.Minor.Value + 1, 0),
					_ => Bound(0, 0, p.Patch!.Value + 1),
				}));
				return true;
			case ">":
				if (p.Major is null) { set.Add(new(Op.Lt, Bound(0, 0, 0))); return true; } // ">*" admits nothing
				set.Add(p.IsFull ? new(Op.Gt, p.Floor()) : new(Op.Ge, p.Ceiling() with { Prerelease = null }));
				return true;
			case ">=":
				if (p.Major is not null) set.Add(new(Op.Ge, p.Floor()));
				return true;
			case "<":
				if (p.Major is null) set.Add(new(Op.Lt, Bound(0, 0, 0)));
				else set.Add(new(Op.Lt, p.IsFull ? p.Floor() : p.Floor() with { Prerelease = "0" }));
				return true;
			default: // "<="
				if (p.Major is not null) set.Add(p.IsFull ? new(Op.Le, p.Floor()) : new(Op.Lt, p.Ceiling()));
				return true;
		}
	}

	private static int? Part(Group g) => !g.Success || g.Value is "x" or "X" or "*" ? null : int.Parse(g.Value, NumberStyles.None, CultureInfo.InvariantCulture);

	/// <summary>The exclusive upper bound <c>X.Y.Z-0</c>, which keeps prereleases of the next line out.</summary>
	private static SemVer Bound(int major, int minor, int patch) => new(major, minor, patch, "0");

	private readonly record struct Partial(int? Major, int? Minor, int? Patch, string? Prerelease)
	{
		public bool IsFull => Major is not null && Minor is not null && Patch is not null;

		public SemVer Floor() => new(Major ?? 0, Minor ?? 0, Patch ?? 0, IsFull ? Prerelease : null);

		/// <summary>The first version past this partial: <c>1.2</c> → <c>1.3.0-0</c>, <c>1</c> → <c>2.0.0-0</c>.</summary>
		public SemVer Ceiling() => Minor is null ? Bound(Major!.Value + 1, 0, 0) : Bound(Major!.Value, Minor.Value + 1, 0);

		public static bool TryParse(string text, out Partial partial)
		{
			partial = default;
			var match = Primitive().Match(text);
			if (!match.Success || match.Groups[1].Success && match.Groups[1].Value != "") return false;
			partial = new Partial(Part(match.Groups[2]), Part(match.Groups[3]), Part(match.Groups[4]), match.Groups[5].Success ? match.Groups[5].Value : null);
			return partial.IsWellFormed;
		}

		/// <summary>"1.x.3" is not a partial: once a part is a wildcard, the rest must be too.</summary>
		public bool IsWellFormed => !(Major is null && (Minor is not null || Patch is not null) || Minor is null && Patch is not null);
	}

	public override string ToString() => Text;
}
