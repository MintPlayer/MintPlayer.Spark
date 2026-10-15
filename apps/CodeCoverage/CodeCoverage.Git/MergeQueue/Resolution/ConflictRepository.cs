using System.Globalization;
using System.Text;

namespace CodeCoverage.Git.MergeQueue.Resolution;

/// <summary>
/// The git access a conflict resolver gets (§10.3): reading blobs, a size first so a huge blob is never read, <c>git
/// merge-file</c> on three blobs, and writing a blob. Every command runs through the hardened <see cref="GitRunner"/> with
/// the resolver's config (empty <c>attr.tree</c>, no hooks, no filters), in the job's repository.
/// </summary>
public sealed class ConflictRepository
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly GitRunner git;
    private readonly string gitDirectory;
    private readonly GitCredential? credential;
    private readonly TimeSpan timeout;
    private readonly IReadOnlyList<KeyValuePair<string, string>> config;
    private readonly CancellationToken cancellationToken;
    private readonly Dictionary<string, byte[]> bytesCache = new(StringComparer.Ordinal);

    internal ConflictRepository(GitRunner git, string gitDirectory, GitCredential? credential, TimeSpan timeout,
        IReadOnlyList<KeyValuePair<string, string>> config, CancellationToken cancellationToken)
    {
        this.git = git;
        this.gitDirectory = gitDirectory;
        this.credential = credential;
        this.timeout = timeout;
        this.config = config;
        this.cancellationToken = cancellationToken;
    }

    /// <summary>A blob's size in bytes.</summary>
    public async Task<long> SizeAsync(string objectName)
    {
        var size = await RequireAsync(["cat-file", "-s", objectName]);
        if (!long.TryParse(size.StandardOutput.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var bytes))
            throw new GitCommandException("cat-file", "the size does not parse.");
        return bytes;
    }

    /// <summary>A blob's text as git's output decoding reads it (lenient), or null when it is over
    /// <paramref name="maxBytes"/>. The take-base key check (O2) has always read sides this way.</summary>
    public async Task<string?> ReadTextAsync(string objectName, long maxBytes)
    {
        if (await SizeAsync(objectName) > maxBytes)
            return null;
        return (await RequireAsync(["cat-file", "blob", objectName])).StandardOutput;
    }

    /// <summary>A blob's exact bytes, or null when it is over <paramref name="maxBytes"/> (asked first, so a huge blob is
    /// never read).</summary>
    public async Task<byte[]?> ReadBytesAsync(string objectName, long maxBytes)
    {
        if (bytesCache.TryGetValue(objectName, out var cached))
            return cached;
        if (await SizeAsync(objectName) > maxBytes)
            return null;
        var (exit, bytes) = await ReadAllAsync(["cat-file", "blob", objectName], maxBytes);
        if (exit != 0 || bytes is null)
            throw new GitCommandException("cat-file", $"exited {exit} while reading a blob.");
        bytesCache[objectName] = bytes;
        return bytes;
    }

    /// <summary>A blob decoded as strict UTF-8 (a byte-order mark stays in the text as U+FEFF, so writing the text back
    /// gives the same bytes), or null when it is not strict UTF-8 or holds a NUL. Throws nothing for a big blob: callers
    /// check the size cap first.</summary>
    public static string? DecodeStrict(byte[] bytes)
    {
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
        return text.Contains('\0') ? null : text;
    }

    /// <summary>
    /// <c>git merge-file --object-id -p --diff3 --diff-algorithm=myers ours base theirs</c> (§10.4 step 1): plain diff3
    /// regions, never zdiff3, whose output keeps the sides' bytes. Answers git's exit code (the number of conflicts, 0 to
    /// 127) and the output, or null output when it is over <paramref name="maxBytes"/>. Any other exit throws.
    /// </summary>
    public async Task<(int Conflicts, byte[]? Output)> MergeFileAsync(string ours, string @base, string theirs, long maxBytes)
    {
        var (exit, output) = await ReadAllAsync(
            ["merge-file", "--object-id", "-p", "--diff3", "--diff-algorithm=myers", "-L", "ours", "-L", "base", "-L", "theirs", ours, @base, theirs],
            maxBytes);
        if (exit is < 0 or > 127)
            throw new GitCommandException("merge-file", $"exited {exit}.");
        return (exit, output);
    }

    /// <summary>Writes <paramref name="text"/> as a blob, bytes as given (UTF-8, no filters), and answers its id.</summary>
    public async Task<string> WriteBlobAsync(string text)
    {
        var result = await RequireAsync(["hash-object", "-w", "--no-filters", "--stdin"], text);
        var oid = result.StandardOutput.Trim();
        return oid.Length == 40 && oid.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
            ? oid
            : throw new GitCommandException("hash-object", "did not print an object id.");
    }

    private GitInvocation Invocation(IReadOnlyList<string> arguments, string? input = null) => new()
    {
        Arguments = arguments,
        GitDirectory = gitDirectory,
        Credential = credential,
        Timeout = timeout,
        StandardInput = input,
        Config = config,
    };

    private async Task<GitResult> RequireAsync(IReadOnlyList<string> arguments, string? input = null)
    {
        var result = await git.RunAsync(Invocation(arguments, input), cancellationToken);
        if (!result.Succeeded)
            throw new GitCommandException(arguments[0], result);
        return result;
    }

    /// <summary>A command's raw stdout (no decoding, so no byte-order mark is lost), bounded: null output when it passes
    /// <paramref name="maxBytes"/>, in which case the process is killed.</summary>
    private async Task<(int ExitCode, byte[]? Output)> ReadAllAsync(IReadOnlyList<string> arguments, long maxBytes)
    {
        await using var read = git.OpenRead(Invocation(arguments));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int n;
        try
        {
            while ((n = await read.Output.ReadAsync(chunk, deadline.Token)) > 0)
            {
                if (buffer.Length + n > maxBytes)
                    return (0, null);
                buffer.Write(chunk, 0, n);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GitCommandException(arguments[0], $"timed out after {timeout}.");
        }
        var result = await read.CompleteAsync(cancellationToken);
        if (result.TimedOut)
            throw new GitCommandException(arguments[0], $"timed out after {timeout}.");
        return (result.ExitCode, buffer.ToArray());
    }
}
