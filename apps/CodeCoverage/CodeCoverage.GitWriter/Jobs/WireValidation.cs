using System.Text.RegularExpressions;
using CodeCoverage.Git;
using CodeCoverage.Git.MergeQueue;

namespace CodeCoverage.GitWriter.Jobs;

/// <summary>
/// Every value of a request is checked here before it reaches a path, an argument or the environment; null means "refuse
/// with a bare 400". Branch names follow <c>git check-ref-format</c>'s rules, narrowed to a conservative character set, so
/// no value can be read as an option, a refspec or a revision expression.
/// </summary>
internal static partial class WireValidation
{
    public const int MaxMessageLength = 64 * 1024;
    public const int MaxGlobs = 64;

    [GeneratedRegex(@"\A[0-9a-f]{40}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Sha();

    [GeneratedRegex(@"\A[a-z]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex Provider();

    [GeneratedRegex(@"\A[0-9A-Za-z_-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RepoId();

    [GeneratedRegex(@"\A[A-Za-z0-9._/-]{1,200}\z", RegexOptions.CultureInvariant)]
    private static partial Regex BranchChars();

    [GeneratedRegex(@"\Arefs/pull/[0-9]{1,10}/head\z", RegexOptions.CultureInvariant)]
    private static partial Regex PullHead();

    public static string? ObjectId(string? value) => value is not null && Sha().IsMatch(value) ? value : null;

    public static bool IsRepo(string? provider, string? repoId)
        => provider is not null && Provider().IsMatch(provider) && repoId is not null && RepoId().IsMatch(repoId);

    /// <summary>A branch name without <c>refs/heads/</c>: no leading <c>-</c>, <c>/</c> or <c>.</c>, no <c>..</c>,
    /// <c>//</c>, <c>@{</c>, no component starting with <c>.</c> or ending in <c>.lock</c>, no trailing <c>/</c> or
    /// <c>.</c>.</summary>
    public static string? Branch(string? value)
    {
        if (value is null || !BranchChars().IsMatch(value) || value[0] is '-' or '/' or '.' || value[^1] is '/' or '.'
            || value.Contains("..", StringComparison.Ordinal) || value.Contains("//", StringComparison.Ordinal) || value.Contains("@{", StringComparison.Ordinal))
            return null;
        foreach (var component in value.Split('/'))
        {
            if (component.StartsWith('.') || component.EndsWith(".lock", StringComparison.Ordinal))
                return null;
        }
        return value;
    }

    /// <summary>A queue branch: a valid branch under <c>mq/</c> (D5). The writer never writes another name through the
    /// prepare or delete endpoints.</summary>
    public static string? QueueBranch(string? value) => Branch(value) is { } branch && branch.StartsWith("mq/", StringComparison.Ordinal) && branch.Length > 3 ? branch : null;

    /// <summary>The ref the pull request's head is fetched from: <c>refs/pull/N/head</c> or <c>refs/heads/&lt;branch&gt;</c>.</summary>
    public static string? HeadRef(string? value)
    {
        if (value is null)
            return null;
        if (PullHead().IsMatch(value))
            return value;
        return value.StartsWith("refs/heads/", StringComparison.Ordinal) && Branch(value["refs/heads/".Length..]) is not null ? value : null;
    }

    /// <summary>An absolute https URL with a host and no user info or fragment. The scheme check is explicit: on Linux
    /// <c>Uri.TryCreate("/x", Absolute)</c> succeeds as <c>file:///x</c>.</summary>
    public static Uri? RemoteUrl(string? value, bool allowInsecureHttp)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out var url))
            return null;
        var schemeOk = url.Scheme == Uri.UriSchemeHttps || (allowInsecureHttp && url.Scheme == Uri.UriSchemeHttp);
        if (!schemeOk || string.IsNullOrEmpty(url.Host) || url.UserInfo.Length > 0 || url.Fragment.Length > 0)
            return null;
        return url;
    }

    /// <summary>The job's credential. Required: the writer never pushes anonymously.</summary>
    public static GitCredential? Credential(CredentialWire? wire)
    {
        if (wire is null || !IsCredentialPart(wire.Username) || !IsCredentialPart(wire.Password) || wire.Username!.Contains(':'))
            return null;
        return new GitCredential(wire.Username!, wire.Password!);
    }

    public static GitIdentity? Identity(IdentityWire? wire)
    {
        if (wire is null || wire.Name is not { Length: <= 256 } name || wire.Email is not { Length: <= 256 } email)
            return null;
        try
        {
            return new GitIdentity(name, email, wire.Date);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public static string? Message(string? value)
        => value is { Length: > 0 and <= MaxMessageLength } && !value.Contains('\0') && !string.IsNullOrWhiteSpace(value) ? value : null;

    /// <summary>The auto-resolve list; an empty list is allowed (every conflict evicts).</summary>
    public static IReadOnlyList<AutoResolveRule>? Rules(List<AutoResolveWire>? wires)
    {
        if (wires is null)
            return [];
        if (wires.Count > MaxGlobs)
            return null;
        var rules = new List<AutoResolveRule>();
        foreach (var wire in wires)
        {
            if (Glob(wire?.Path) is not { } glob)
                return null;
            if (wire!.AnyLine)
            {
                if (wire.Keys is { Count: > 0 })
                    return null;
                rules.Add(AutoResolveRule.WholeFile(glob));
                continue;
            }
            if (wire.Keys is not { Count: > 0 and <= MaxGlobs } keys || keys.Any(k => string.IsNullOrEmpty(k) || k.Length > 256 || k.Any(char.IsControl)))
                return null;
            rules.Add(new AutoResolveRule(glob, keys));
        }
        return rules;
    }

    public static IReadOnlyList<PathGlob>? Globs(List<string>? values)
    {
        if (values is not { Count: > 0 and <= MaxGlobs })
            return null;
        var globs = new List<PathGlob>();
        foreach (var value in values)
        {
            if (Glob(value) is not { } glob)
                return null;
            globs.Add(new PathGlob(glob));
        }
        return globs;
    }

    private static string? Glob(string? value)
        => value is { Length: > 0 and <= 256 } && !value.Any(c => char.IsControl(c) || c == '\\') && !value.StartsWith('/') && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static bool IsCredentialPart(string? value)
        => !string.IsNullOrEmpty(value) && value.Length <= 4096 && !value.Any(char.IsControl);
}
