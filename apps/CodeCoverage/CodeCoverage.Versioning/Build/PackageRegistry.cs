using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using CodeCoverage.Versioning.Api;
using CodeCoverage.Versioning.Graph;
using CodeCoverage.Versioning.Validation;

namespace CodeCoverage.Versioning.Build;

/// <summary>
/// The normalized API listing of a version already published (PRD D6, S7; #693 S8): its hash stands in for a last tag
/// whose annotation carries none, and its lines give the Public API section.
/// </summary>
public interface IPackageRegistry
{
	/// <returns>The listing as <see cref="BuildManifest"/> builds it, or null when the registry does not have that version.</returns>
	Task<IReadOnlyList<string>?> ApiListingAsync(ProjectNode package, string version, CancellationToken cancellationToken = default);
}

/// <summary>
/// nuget.org's flat container and registry.npmjs.org, anonymously. A package is read as an archive and never unpacked
/// to disk or run: only <c>lib/&lt;tfm&gt;/&lt;assembly&gt;.dll</c> metadata (nupkg) or <c>.d.ts</c> text (npm tarball) is
/// read, hashed the same way <see cref="BuildManifest"/> hashes the build, and every read is size-capped.
/// </summary>
public sealed class PublicPackageRegistry(HttpClient http) : IPackageRegistry
{
	public const long MaxPackageBytes = 64 * 1024 * 1024;
	public const long MaxEntryBytes = 16 * 1024 * 1024;

	/// <summary>The hash of <see cref="ApiListingAsync"/>, as the build hashes its own listing.</summary>
	public async Task<string?> ApiHashAsync(ProjectNode package, string version, CancellationToken cancellationToken = default)
		=> await ApiListingAsync(package, version, cancellationToken) is { } lines ? DotnetApiListing.Hash(lines) : null;

	public async Task<IReadOnlyList<string>?> ApiListingAsync(ProjectNode package, string version, CancellationToken cancellationToken = default)
	{
		if (package.Id is not { } id || !Identifiers.IsPackageId(id) || version.Length > 128 || version.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+')))
			return null;
		var url = package.Kind == PackageKind.NuGet
			? $"https://api.nuget.org/v3-flatcontainer/{id.ToLowerInvariant()}/{version.ToLowerInvariant()}/{id.ToLowerInvariant()}.{version.ToLowerInvariant()}.nupkg"
			: $"https://registry.npmjs.org/{id}/-/{id[(id.LastIndexOf('/') + 1)..]}-{version}.tgz";

		using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
		if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden) return null;
		response.EnsureSuccessStatusCode();
		if (response.Content.Headers.ContentLength > MaxPackageBytes) throw new InvalidDataException("The package is larger than the registry read allows.");

		var buffer = new MemoryStream();
		await using (var body = await response.Content.ReadAsStreamAsync(cancellationToken))
			await CopyCappedAsync(body, buffer, MaxPackageBytes, cancellationToken);
		buffer.Position = 0;
		return package.Kind == PackageKind.NuGet ? NuGetListing(buffer, package) : await NpmListingAsync(buffer, cancellationToken);
	}

	internal static string? NuGetHash(Stream nupkg, ProjectNode package) => NuGetListing(nupkg, package) is { } lines ? DotnetApiListing.Hash(lines) : null;

	internal static async Task<string?> NpmHashAsync(Stream tgz, CancellationToken cancellationToken)
		=> await NpmListingAsync(tgz, cancellationToken) is { } lines ? TypeScriptApiListing.Hash(lines) : null;

	internal static IReadOnlyList<string>? NuGetListing(Stream nupkg, ProjectNode package)
	{
		var assembly = (package.AssemblyName ?? package.Id!) + ".dll";
		using var zip = new ZipArchive(nupkg, ZipArchiveMode.Read);
		var lines = new List<string>();
		foreach (var entry in zip.Entries.OrderBy(e => e.FullName, StringComparer.Ordinal))
			if (entry.FullName.Split('/') is ["lib", var tfm, var file] && file.Equals(assembly, StringComparison.OrdinalIgnoreCase) && Identifiers.IsTargetFramework(tfm))
				lines.AddRange(List(entry, tfm));

		// A source generator or analyzer ships its assembly under analyzers/dotnet/cs instead. The nupkg does not record the
		// framework it was built for, and the build prefixes each line with it, so it is read from the project: one tfm.
		// It sits in analyzers/dotnet/cs, or in a Roslyn-versioned analyzers/dotnet/roslyn<x.y>/cs (#693 S8: Spark's own
		// generator does); with several, the newest Roslyn's copy is the one the current SDK loads.
		if (lines.Count == 0 && package.TargetFrameworks is [var analyzerTfm] && Identifiers.IsTargetFramework(analyzerTfm)
			&& zip.Entries
				.Select(e => (Entry: e, Roslyn: AnalyzerRoslyn(e.FullName, assembly)))
				.Where(a => a.Roslyn is not null)
				.OrderByDescending(a => a.Roslyn)
				.Select(a => a.Entry)
				.FirstOrDefault() is { } analyzer)
			lines.AddRange(List(analyzer, analyzerTfm));
		return lines.Count == 0 ? null : lines;
	}

	/// <summary>
	/// The Roslyn version of an analyzer entry for <paramref name="assembly"/>: 0.0 for the unversioned folder, null for
	/// any other entry.
	/// </summary>
	private static Version? AnalyzerRoslyn(string path, string assembly) => path.Split('/') switch
	{
		["analyzers", "dotnet", "cs", var file] when file.Equals(assembly, StringComparison.OrdinalIgnoreCase) => new Version(0, 0),
		["analyzers", "dotnet", var folder, "cs", var file] when file.Equals(assembly, StringComparison.OrdinalIgnoreCase)
			&& folder.StartsWith("roslyn", StringComparison.Ordinal) && Version.TryParse(folder["roslyn".Length..], out var version) => version,
		_ => null,
	};

	private static IEnumerable<string> List(ZipArchiveEntry entry, string tfm)
	{
		if (entry.Length > MaxEntryBytes) throw new InvalidDataException($"{entry.FullName} is larger than the registry read allows.");
		using var copy = new MemoryStream();
		using (var stream = entry.Open()) stream.CopyTo(copy);
		copy.Position = 0;
		return DotnetApiListing.List(copy).Select(l => $"{tfm} {l}").ToList();
	}

	internal static async Task<IReadOnlyList<string>?> NpmListingAsync(Stream tgz, CancellationToken cancellationToken)
	{
		var files = new List<(string Path, string Text)>();
		await using var gzip = new GZipStream(tgz, CompressionMode.Decompress);
		using var capped = new MemoryStream();
		await CopyCappedAsync(gzip, capped, MaxPackageBytes, cancellationToken);
		capped.Position = 0;
		using var tar = new TarReader(capped);
		while (await tar.GetNextEntryAsync(cancellationToken: cancellationToken) is { } entry)
		{
			// npm packs everything under one top folder, "package/" by convention; paths are relative to it.
			var slash = entry.Name.IndexOf('/');
			var path = slash < 0 ? entry.Name : entry.Name[(slash + 1)..];
			if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || !path.EndsWith(".d.ts", StringComparison.Ordinal)
				|| path.Split('/').Contains("node_modules") || path.Split('/').Contains("..") || entry.DataStream is null)
				continue;
			if (entry.Length > BuildManifest.MaxDeclarationBytes) continue;
			using var reader = new StreamReader(entry.DataStream, Encoding.UTF8);
			files.Add((path, await reader.ReadToEndAsync(cancellationToken)));
			if (files.Count > BuildManifest.MaxDeclarationFiles) break;
		}
		return files.Count == 0 ? null : TypeScriptApiListing.List(files);
	}

	private static async Task CopyCappedAsync(Stream source, Stream target, long max, CancellationToken cancellationToken)
	{
		var buffer = new byte[81920];
		long total = 0;
		int read;
		while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
		{
			total += read;
			if (total > max) throw new InvalidDataException("The package is larger than the registry read allows.");
			await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
		}
	}
}
