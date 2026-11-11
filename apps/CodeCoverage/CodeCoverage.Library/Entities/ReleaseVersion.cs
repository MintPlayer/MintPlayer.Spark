using System.Security.Cryptography;
using System.Text;

namespace CodeCoverage.Entities;

/// <summary>
/// A version someone asked a release line to ship (segment P, P5): named in <c>/release … Pkg@ver</c>, or pushed by hand
/// as the tag <c>&lt;PackageId&gt;/v&lt;Version&gt;</c> in a repository whose current lines are promoted.
/// </summary>
/// <remarks>
/// <para>A pushed tag is deleted when it is recorded, and the App puts it back on the promotion commit of its line, in the
/// promotion's own push. <see cref="PlacedCommit"/> is then set, and that is what the line's versioning step asks the
/// server about (<c>POST /api/versioning/tags/verify</c>): a hand tag on the released commit that is not recorded as
/// placed there fails the release.</para>
/// <para>Loaded by id prefix only (<see cref="Prefix"/>): a repository has a handful, so no index is needed.</para>
/// </remarks>
public class ReleaseVersion
{
    public string? Id { get; set; }

    public string Repository { get; set; } = string.Empty;

    public string PackageId { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    /// <summary>The tag name, <c>&lt;PackageId&gt;/v&lt;Version&gt;</c>.</summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>Who asked for it: the commenter or the tag's pusher.</summary>
    public string Actor { get; set; } = string.Empty;

    /// <summary><see cref="FromComment"/> or <see cref="FromTag"/>.</summary>
    public string Source { get; set; } = string.Empty;

    public DateTime RecordedUtc { get; set; }

    /// <summary>The promotion commit the tag was pushed on; null while the version waits for its line's next promotion.</summary>
    public string? PlacedCommit { get; set; }

    public DateTime? PlacedUtc { get; set; }

    public const string FromComment = "comment";
    public const string FromTag = "tag";

    /// <summary><c>ReleaseVersions/{provider}/{repositoryId}/</c>: every recorded version of a repository.</summary>
    public static string Prefix(string repositoryId) => $"ReleaseVersions/{MergeQueue.RepositorySuffix(repositoryId)}/";

    /// <summary>The id of <paramref name="tag"/>'s record, a hash of the tag so an npm scope's <c>@</c> and <c>/</c> stay out of it.</summary>
    public static string DocumentId(string repositoryId, string tag)
        => Prefix(repositoryId) + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(tag)))[..16];
}
