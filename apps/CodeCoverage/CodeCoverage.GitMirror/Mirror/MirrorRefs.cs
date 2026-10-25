using System.Security.Cryptography;
using System.Text;
using CodeCoverage.Git;

namespace CodeCoverage.GitMirror.Mirror;

/// <summary>One branch or tag of a mirror. <see cref="Sha"/> is the commit it points at (an annotated tag peeled).</summary>
/// <param name="Run">An annotated tag's <c>run:</c> trailer, the workflow run that wrote a version tag (#544 D5), as
/// written: the app validates it.</param>
public sealed record MirrorRef(string Name, string Kind, string Sha, string? Run = null);

/// <summary>A mirror's refs and their hash: hex SHA-256 over the sorted <c>{objectname} {refname}</c> lines of
/// <c>for-each-ref refs/heads refs/tags</c> (contract). Any push, force-push, new or deleted ref changes it.</summary>
public sealed record MirrorRefsState(string RefsHash, IReadOnlyList<MirrorRef> Refs)
{
    private const int MaxRunLength = 512;

    public static async Task<MirrorRefsState?> ReadAsync(GitRunner git, string mirror, CancellationToken cancellationToken)
    {
        var result = await git.RunAsync(new GitInvocation
        {
            GitDirectory = mirror,
            Read = true,
            // Only an annotated tag's `run` trailer is read, never its whole message, which can be long (release notes).
            Arguments = ["for-each-ref", "--format=%(objectname)%09%(refname)%09%(*objectname)%09%(if)%(*objectname)%(then)%(contents:trailers:key=run,valueonly,separator=%x20)%(end)",
                "refs/heads", "refs/tags"],
        }, cancellationToken);
        if (!result.Succeeded)
            return null;

        var lines = new List<string>();
        var refs = new List<MirrorRef>();
        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 2)
                continue;
            lines.Add($"{parts[0]} {parts[1]}");
            var peeled = parts.Length > 2 && parts[2].Length > 0 ? parts[2] : parts[0];
            var run = parts.Length > 3 && parts[3].Trim() is { Length: > 0 and <= MaxRunLength } trailer ? trailer : null;
            refs.Add(new MirrorRef(parts[1], parts[1].StartsWith("refs/tags/", StringComparison.Ordinal) ? "tag" : "head", peeled, run));
        }
        lines.Sort(StringComparer.Ordinal);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));
        refs.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return new MirrorRefsState(hash, refs);
    }
}
