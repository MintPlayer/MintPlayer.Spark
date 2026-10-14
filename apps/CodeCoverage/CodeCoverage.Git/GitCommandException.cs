namespace CodeCoverage.Git;

/// <summary>A git command failed in a way that is an operational error (a timeout, an unexpected exit code, output
/// that does not parse), not a verdict about the content being merged. The caller retries or reports; it never evicts
/// a pull request on this.</summary>
public sealed class GitCommandException : Exception
{
    public GitCommandException(string verb, GitResult result)
        : base($"git {verb} {(result.TimedOut ? "timed out" : $"exited with {result.ExitCode}")}: {result.StandardError.Trim()}")
    {
        Verb = verb;
        Result = result;
    }

    public GitCommandException(string verb, string message) : base($"git {verb}: {message}")
    {
        Verb = verb;
    }

    public string Verb { get; }

    /// <summary>The failed run (stderr already scrubbed of any credential), when there was one.</summary>
    public GitResult? Result { get; }
}
