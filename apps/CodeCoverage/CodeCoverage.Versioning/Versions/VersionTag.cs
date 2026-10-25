using System.Reflection;
using System.Text;
using CodeCoverage.Versioning.Validation;

namespace CodeCoverage.Versioning.Versions;

/// <summary>
/// One release of one package: the annotated tag <c>&lt;PackageId&gt;/v&lt;version&gt;</c> (PRD D1), e.g.
/// <c>MintPlayer.Mapper/v13.2.0</c> or <c>@mintplayer/ng-spark/v22.4.0</c>. The tag is the only version record.
/// </summary>
public sealed record VersionTag(string PackageId, PackageVersion Version)
{
	public const string Separator = "/v";

	public string Name => PackageId + Separator + Version;

	/// <summary>The prefix every tag of a package starts with, MinVer's <c>MinVerTagPrefix</c> (D9).</summary>
	public static string Prefix(string packageId) => packageId + Separator;

	/// <summary>
	/// Reads a tag name. The split is at the last <c>/v</c>, since a version never contains a slash while an npm scope
	/// does. A name whose id is not a package id or whose version is not one of ours is not a version tag: other tags
	/// in the repository (<c>v1.0</c>, a human's <c>MintPlayer.Mapper/v13.2</c>) are simply ignored.
	/// </summary>
	public static bool TryParse(string? name, out VersionTag tag)
	{
		tag = null!;
		if (name is null || !Identifiers.IsRefName(name)) return false;
		var split = name.LastIndexOf(Separator, StringComparison.Ordinal);
		if (split <= 0) return false;
		var id = name[..split];
		if (!Identifiers.IsPackageId(id) || !PackageVersion.TryParse(name[(split + Separator.Length)..], out var version)) return false;
		tag = new VersionTag(id, version);
		return true;
	}

	/// <summary>Builds the tag for a release, refusing an id that would not survive as a ref name (D5).</summary>
	public static VersionTag For(string packageId, PackageVersion version)
	{
		var tag = new VersionTag(packageId, version);
		if (!Identifiers.IsPackageId(packageId) || !Identifiers.IsRefName(tag.Name))
			throw new VersioningException($"'{packageId}' is not a NuGet package id or npm package name that can be tagged.");
		return tag;
	}

	public override string ToString() => Name;
}

/// <summary>
/// The message of an annotated version tag (PRD D6): a title line, then <c>api-hash: sha256:…</c> and the engine
/// version that computed it. Both fields are optional when read back, since a human's tag or a seed tag carries neither.
/// <see cref="Run"/> is the workflow run that wrote the tag (#544 D5); a tag from before it, or a seed tag, has none.
/// </summary>
public sealed record TagAnnotation(string? ApiHash, string? EngineVersion, string? Run = null)
{
	public const string ApiHashKey = "api-hash:";
	public const string EngineKey = "engine:";
	public const string RunKey = "run:";

	/// <summary>The run's page from the runner's <c>GITHUB_SERVER_URL</c>, <c>GITHUB_REPOSITORY</c> and <c>GITHUB_RUN_ID</c>; null when any is unusable.</summary>
	public static string? RunUrl(string? server, string? repository, string? runId)
		=> $"{server?.TrimEnd('/')}/{repository}/actions/runs/{runId}" is var url && Identifiers.IsRunUrl(url) ? url : null;

	public string Format(VersionTag tag)
	{
		var text = new StringBuilder().Append(tag.PackageId).Append(' ').Append(tag.Version).Append("\n\n");
		if (ApiHash is not null)
		{
			if (!Identifiers.IsApiHash(ApiHash)) throw new VersioningException($"'{ApiHash}' is not an API hash of the form sha256:<64 hex>.");
			text.Append(ApiHashKey).Append(' ').Append(ApiHash).Append('\n');
		}
		text.Append(EngineKey).Append(" CodeCoverage.Versioning ").Append(EngineVersion ?? Engine.Version).Append('\n');
		if (Run is not null)
		{
			if (!Identifiers.IsRunUrl(Run)) throw new VersioningException($"'{Run}' is not a workflow run's URL.");
			text.Append(RunKey).Append(' ').Append(Run).Append('\n');
		}
		return text.ToString();
	}

	/// <summary>
	/// Reads a tag message. A hash that is not well formed counts as absent, so the previous hash falls back to the
	/// registry (D6) rather than comparing against garbage.
	/// </summary>
	public static TagAnnotation Parse(string? message)
	{
		string? hash = null, engine = null, run = null;
		foreach (var raw in (message ?? "").Split('\n'))
		{
			var line = raw.Trim();
			if (line.StartsWith(ApiHashKey, StringComparison.Ordinal))
			{
				var value = line[ApiHashKey.Length..].Trim();
				hash = Identifiers.IsApiHash(value) ? value : null;
			}
			else if (line.StartsWith(EngineKey, StringComparison.Ordinal))
			{
				var value = line[EngineKey.Length..].Trim();
				engine = value.StartsWith("CodeCoverage.Versioning ", StringComparison.Ordinal) ? value["CodeCoverage.Versioning ".Length..] : value;
			}
			else if (line.StartsWith(RunKey, StringComparison.Ordinal))
			{
				var value = line[RunKey.Length..].Trim();
				run = Identifiers.IsRunUrl(value) ? value : null;
			}
		}
		return new TagAnnotation(hash, engine, run);
	}
}

/// <summary>The engine's own version, written into every annotation.</summary>
public static class Engine
{
	public static string Version { get; } = Read();

	private static string Read()
	{
		var informational = typeof(Engine).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
		// Drop the "+<commit>" source-link suffix: the annotation names the engine, not the build.
		return informational?.Split('+')[0] ?? "0.0.0";
	}
}

/// <summary>A rule of the versioning engine refused the input. The message is shown to the repository's owner as is.</summary>
public sealed class VersioningException(string message) : Exception(message);
