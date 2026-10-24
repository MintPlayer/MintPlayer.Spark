using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CodeCoverage.Versioning.Versions;

/// <summary>
/// The prerelease labels a version of ours may carry (PRD D5, D8), in order of maturity: the least mature one wins when
/// several upstreams are in prerelease. The order is also semver's alphabetical order, so tools that sort our versions
/// agree with us. On the wire (the settings the app sends the git writer) a label is its text, never its number.
/// </summary>
[JsonConverter(typeof(LabelJsonConverter))]
public enum PrereleaseLabel
{
	Alpha,
	Beta,
	Next,
	Preview,
	Rc,
}

/// <summary>
/// A version the engine writes or reads back from one of its own tags: <c>X.Y.Z</c> or <c>X.Y.Z-&lt;label&gt;.N</c>, and
/// nothing else (PRD D5). Build metadata, other labels and leading zeros are refused, so every tag sorts the same way in
/// git, NuGet and npm.
/// </summary>
public sealed partial record PackageVersion(int Major, int Minor, int Patch, PrereleaseLabel? Label = null, int Number = 0)
	: IComparable<PackageVersion>
{
	[GeneratedRegex(@"^(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})(?:-(alpha|beta|next|preview|rc)\.(0|[1-9][0-9]{0,8}))?\z", RegexOptions.CultureInvariant)]
	private static partial Regex Grammar();

	public bool IsPrerelease => Label is not null;

	/// <summary>The version without its prerelease part.</summary>
	public PackageVersion Core => this with { Label = null, Number = 0 };

	public static bool TryParse(string? text, out PackageVersion version)
	{
		version = null!;
		if (text is null || text.Length > 64) return false;
		var match = Grammar().Match(text);
		if (!match.Success) return false;

		static int Int(Group g) => int.Parse(g.Value, NumberStyles.None, CultureInfo.InvariantCulture);
		version = new PackageVersion(
			Int(match.Groups[1]), Int(match.Groups[2]), Int(match.Groups[3]),
			match.Groups[4].Success ? Labels.Parse(match.Groups[4].Value) : null,
			match.Groups[5].Success ? Int(match.Groups[5]) : 0);
		return true;
	}

	public static PackageVersion Parse(string text)
		=> TryParse(text, out var version) ? version : throw new FormatException($"'{text}' is not a version of the form X.Y.Z or X.Y.Z-<alpha|beta|next|preview|rc>.N.");

	public int CompareTo(PackageVersion? other)
	{
		if (other is null) return 1;
		var c = Major.CompareTo(other.Major);
		if (c == 0) c = Minor.CompareTo(other.Minor);
		if (c == 0) c = Patch.CompareTo(other.Patch);
		if (c != 0) return c;
		// A stable version sorts above every prerelease of the same core.
		if (Label is null || other.Label is null) return (Label is null).CompareTo(other.Label is null);
		c = Label.Value.CompareTo(other.Label.Value);
		return c != 0 ? c : Number.CompareTo(other.Number);
	}

	public static bool operator <(PackageVersion left, PackageVersion right) => left.CompareTo(right) < 0;
	public static bool operator >(PackageVersion left, PackageVersion right) => left.CompareTo(right) > 0;
	public static bool operator <=(PackageVersion left, PackageVersion right) => left.CompareTo(right) <= 0;
	public static bool operator >=(PackageVersion left, PackageVersion right) => left.CompareTo(right) >= 0;

	public override string ToString() => Label is { } label
		? string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}-{Labels.Format(label)}.{Number}")
		: string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
}

public static class Labels
{
	public static string Format(PrereleaseLabel label) => label switch
	{
		PrereleaseLabel.Alpha => "alpha",
		PrereleaseLabel.Beta => "beta",
		PrereleaseLabel.Next => "next",
		PrereleaseLabel.Preview => "preview",
		PrereleaseLabel.Rc => "rc",
		_ => throw new ArgumentOutOfRangeException(nameof(label)),
	};

	public static PrereleaseLabel Parse(string text) => text switch
	{
		"alpha" => PrereleaseLabel.Alpha,
		"beta" => PrereleaseLabel.Beta,
		"next" => PrereleaseLabel.Next,
		"preview" => PrereleaseLabel.Preview,
		"rc" => PrereleaseLabel.Rc,
		_ => throw new FormatException($"'{text}' is not one of alpha, beta, next, preview, rc."),
	};

	/// <summary>
	/// The label an upstream prerelease is mirrored as (PRD D8): its first prerelease identifier when that is on the
	/// allow-list, ignoring case and a trailing number (<c>rc1</c>, <c>RC.2</c>, <c>preview.7.24407.12</c>). Anything
	/// else (<c>canary</c>, <c>dev</c>, <c>experimental</c>) is treated as <see cref="PrereleaseLabel.Alpha"/>: an
	/// unknown label is assumed to be the least mature, so a release never claims more stability than its upstream.
	/// </summary>
	public static PrereleaseLabel Mirror(string prerelease)
	{
		var first = prerelease.Split('.', 2)[0].TrimEnd("0123456789".ToCharArray()).TrimEnd('-').ToLowerInvariant();
		return first switch
		{
			"beta" => PrereleaseLabel.Beta,
			"next" => PrereleaseLabel.Next,
			"preview" => PrereleaseLabel.Preview,
			"rc" => PrereleaseLabel.Rc,
			_ => PrereleaseLabel.Alpha,
		};
	}

	/// <summary>
	/// The least mature label among resolved upstream versions (the anchor and the runtime dependencies), or null when
	/// all of them are stable. Text that is not a semver version is skipped: a resolved version is data from a
	/// lockfile or the release manifest, and a broken one must not decide the label.
	/// </summary>
	public static PrereleaseLabel? LeastMature(IEnumerable<string> resolvedVersions)
	{
		PrereleaseLabel? least = null;
		foreach (var text in resolvedVersions)
		{
			if (!SemVer.TryParse(text, out var version) || version.Prerelease is not { } pre) continue;
			var label = Mirror(pre);
			if (least is null || label < least) least = label;
		}
		return least;
	}
}

/// <summary>A label on the wire is its text (<c>"preview"</c>); anything else, a number included, is refused.</summary>
public sealed class LabelJsonConverter : JsonConverter<PrereleaseLabel>
{
	public override PrereleaseLabel Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		if (reader.TokenType != JsonTokenType.String) throw new JsonException("A prerelease label must be a string.");
		try { return Labels.Parse(reader.GetString()!); }
		catch (FormatException e) { throw new JsonException(e.Message, e); }
	}

	public override void Write(Utf8JsonWriter writer, PrereleaseLabel value, JsonSerializerOptions options)
		=> writer.WriteStringValue(Labels.Format(value));
}
