using System.Text.RegularExpressions;
using CodeCoverage.Git;
using CodeCoverage.Git.MergeQueue;

namespace CodeCoverage.GitWriter.Jobs;

/// <summary>
/// Every value of a request is checked here before it reaches a path, an argument or the environment; null means "refuse
/// with a 400 naming the field" (<see cref="Refusal"/>). The rules the app must also hold its configuration to (branch
/// names, globs, keys, messages, identities) are <see cref="MergeQueueWireRules"/>, shared with it so the two cannot drift
/// (§0 D57).
/// </summary>
internal static partial class WireValidation
{
    public const int MaxMessageLength = MergeQueueWireRules.MaxMessageLength;
    public const int MaxGlobs = MergeQueueWireRules.MaxGlobs;

    // The rule each field is refused under, as the 400 names it. Never a value: the credential is one of them.
    public const string RepoRule = "provider: lowercase letters; repoId: 1 to 64 letters, digits, '_' or '-'";
    public const string RemoteUrlRule = "an absolute https URL of at most 2048 characters with a host, no user info and no fragment";
    public const string CredentialRule = "a user name without ':' and a password, each 1 to 4096 characters without a control character";
    public const string ShaRule = "a commit id of 40 lowercase hex characters";
    public const string HeadRefRule = "refs/pull/<n>/head or refs/heads/<branch>";
    public const string QueueBranchRule = "a branch under mq/: " + MergeQueueWireRules.BranchRule;

    public static readonly string AutoResolveEntriesRule =
        $"at most {MaxGlobs} entries, each a path ({MergeQueueWireRules.GlobRule}) with 1 to {MaxGlobs} keys ({MergeQueueWireRules.KeyRule}), or anyLine and no keys";

    public static readonly string RegeneratedPathsRule = $"at most {MaxGlobs} globs, each {MergeQueueWireRules.GlobRule}";

    [GeneratedRegex(@"\A[0-9a-f]{40}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Sha();

    [GeneratedRegex(@"\A[a-z]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex Provider();

    [GeneratedRegex(@"\A[0-9A-Za-z_-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RepoId();

    [GeneratedRegex(@"\Arefs/pull/[0-9]{1,10}/head\z", RegexOptions.CultureInvariant)]
    private static partial Regex PullHead();

    public static string? ObjectId(string? value) => value is not null && Sha().IsMatch(value) ? value : null;

    public static bool IsRepo(string? provider, string? repoId)
        => provider is not null && Provider().IsMatch(provider) && repoId is not null && RepoId().IsMatch(repoId);

    /// <summary>A branch name without <c>refs/heads/</c> (<see cref="MergeQueueWireRules.IsBranch"/>).</summary>
    public static string? Branch(string? value) => MergeQueueWireRules.IsBranch(value) ? value : null;

    /// <summary>A queue branch: a valid branch under <c>mq/</c> (D5). The writer never writes another name through the
    /// prepare or delete endpoints.</summary>
    public static string? QueueBranch(string? value) => MergeQueueWireRules.IsQueueBranch(value) ? value : null;

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
        if (wire is null || !MergeQueueWireRules.IsIdentity(wire.Name, wire.Email))
            return null;
        try
        {
            return new GitIdentity(wire.Name!, wire.Email!, wire.Date);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public static string? Message(string? value) => MergeQueueWireRules.IsMessage(value) ? value : null;

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
            if (wire is null || !MergeQueueWireRules.IsGlob(wire.Path))
                return null;
            if (wire.AnyLine)
            {
                if (wire.Keys is { Count: > 0 })
                    return null;
                rules.Add(AutoResolveRule.WholeFile(wire.Path!));
                continue;
            }
            if (wire.Keys is not { Count: > 0 and <= MaxGlobs } keys || !keys.All(MergeQueueWireRules.IsKey))
                return null;
            rules.Add(new AutoResolveRule(wire.Path!, keys));
        }
        return rules;
    }

    /// <summary>
    /// <c>regeneratedPaths</c>: at most <see cref="MaxGlobs"/>, and none is fine (§0 D57). A repository without a regenerate
    /// step has none and publishes no patch; a patch against an empty list is refused as outside it (T4).
    /// </summary>
    public static IReadOnlyList<PathGlob>? Globs(List<string>? values)
    {
        if (values is null)
            return [];
        if (values.Count > MaxGlobs || !values.All(MergeQueueWireRules.IsGlob))
            return null;
        return [.. values.Select(value => new PathGlob(value))];
    }

    private static bool IsCredentialPart(string? value)
        => !string.IsNullOrEmpty(value) && value.Length <= 4096 && !value.Any(char.IsControl);
}
