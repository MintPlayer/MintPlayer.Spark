using System.Text;
using CodeCoverage.Versioning.Git;
using CodeCoverage.Versioning.Versions;

namespace CodeCoverage.Versioning.Releases;

/// <summary>One version tag of a package: where it points and what its annotation recorded (D6).</summary>
public sealed record TaggedVersion(VersionTag Tag, string Commit, TagAnnotation? Annotation)
{
	public PackageVersion Version => Tag.Version;
}

/// <summary>Every version tag of the repository, by package. Tags that are not <c>&lt;id&gt;/v&lt;version&gt;</c> are ignored.</summary>
public sealed class TagHistory(IEnumerable<TaggedVersion> tags)
{
	private readonly ILookup<string, TaggedVersion> byPackage = tags.ToLookup(t => t.Tag.PackageId, StringComparer.Ordinal);

	public IReadOnlyList<TaggedVersion> Of(string packageId) => byPackage[packageId].OrderBy(t => t.Version).ToList();

	/// <summary>The tags a person pushed on <paramref name="commit"/>: fixed versions for this run (D3).</summary>
	public IEnumerable<TaggedVersion> On(string commit) => byPackage.SelectMany(g => g).Where(t => t.Commit == commit);

	public static TagHistory Empty { get; } = new([]);

	/// <summary>Reads the tags through <c>for-each-ref</c>; annotated tags are peeled to their commit.</summary>
	public static async Task<TagHistory> ReadAsync(GitCli git, CancellationToken cancellationToken = default)
	{
		// Fields split by NUL, records by the ASCII record separator: tag messages hold newlines.
		var result = (await git.RunAsync(["for-each-ref", "--format=%(refname:strip=2)%00%(objectname)%00%(*objectname)%00%(contents)%1e", "refs/tags"],
			cancellationToken: cancellationToken)).EnsureSuccess("for-each-ref");
		var tags = new List<TaggedVersion>();
		foreach (var record in Encoding.UTF8.GetString(result.Output).Split('\x1e'))
		{
			var fields = record.TrimStart('\n').Split('\0');
			if (fields.Length < 4 || !VersionTag.TryParse(fields[0], out var tag)) continue;
			var annotated = GitCli.IsObjectId(fields[2]);
			var commit = annotated ? fields[2] : fields[1];
			if (!GitCli.IsObjectId(commit)) continue;
			tags.Add(new TaggedVersion(tag, commit, annotated ? TagAnnotation.Parse(fields[3]) : null));
		}
		return new TagHistory(tags);
	}
}
