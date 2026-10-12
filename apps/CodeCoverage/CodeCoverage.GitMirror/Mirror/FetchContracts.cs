namespace CodeCoverage.GitMirror.Mirror;

// The wire shapes of POST /repos/{provider}/{repoId}/fetch (the sidecar contract). JSON camelCase.

public sealed class FetchRequestWire
{
    public string? CloneUrl { get; set; }
    public CredentialWire? Credential { get; set; }
    public QuotaWire? Quota { get; set; }

    /// <summary><c>clone-or-fetch</c> (absent: the default) or <c>fetch-only</c>, see <see cref="FetchModes"/>.</summary>
    public string? Mode { get; set; }
}

/// <summary>
/// Whether a fetch may create a mirror (D10, D40). Only a graph view may: every other trigger (push, repository added,
/// the hourly safety net) sends <c>fetch-only</c>, which answers <c>refused</c>/<c>missing</c> without starting git when
/// there is no mirror, so a lost volume is not re-cloned for repositories nobody views.
/// </summary>
public static class FetchModes
{
    public const string CloneOrFetch = "clone-or-fetch";
    public const string FetchOnly = "fetch-only";

    /// <summary>True for <c>fetch-only</c>, false for <c>clone-or-fetch</c> or absent, null for anything else.</summary>
    public static bool? IsFetchOnly(string? mode) => mode switch
    {
        null or CloneOrFetch => false,
        FetchOnly => true,
        _ => null,
    };
}

public sealed class CredentialWire
{
    public string? Username { get; set; }
    public string? Password { get; set; }

    public override string ToString() => $"CredentialWire {{ Username = {Username}, Password = *** }}";
}

public sealed class QuotaWire
{
    public long? MaxBytes { get; set; }
    public int? MaxRefs { get; set; }
    public int? TimeoutSeconds { get; set; }
}

/// <summary>A fetch's quota (D7). Absent values take the anonymous placeholders, the stricter set.</summary>
public sealed record FetchQuota(long MaxBytes, int MaxRefs, TimeSpan Timeout)
{
    public static readonly FetchQuota AnonymousDefault = new(500L * 1024 * 1024, 10_000, TimeSpan.FromMinutes(3));

    /// <summary>The upper bound a caller may ask for: anything larger is clamped, so a bad request cannot pin the
    /// sidecar for hours.</summary>
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromHours(1);

    public static FetchQuota From(QuotaWire? wire)
    {
        if (wire is null)
            return AnonymousDefault;
        var seconds = wire.TimeoutSeconds is > 0 ? Math.Min(wire.TimeoutSeconds.Value, (int)MaxTimeout.TotalSeconds) : (int)AnonymousDefault.Timeout.TotalSeconds;
        return new FetchQuota(
            wire.MaxBytes is > 0 ? wire.MaxBytes.Value : AnonymousDefault.MaxBytes,
            wire.MaxRefs is > 0 ? wire.MaxRefs.Value : AnonymousDefault.MaxRefs,
            TimeSpan.FromSeconds(seconds));
    }
}

public static class FetchOutcomes
{
    public const string Cloned = "cloned";
    public const string Fetched = "fetched";
    public const string Refused = "refused";
    public const string Failed = "failed";
}

public static class FetchReasons
{
    public const string TooManyRefs = "too-many-refs";
    public const string TooLarge = "too-large";
    public const string DiskFloor = "disk-floor";
    public const string Timeout = "timeout";
    public const string Auth = "auth";
    public const string NotFound = "not-found";
    public const string Error = "error";

    /// <summary>A <c>fetch-only</c> request found no mirror (D40).</summary>
    public const string Missing = "missing";
}

public sealed record FetchResponse(
    string Outcome,
    string? Reason,
    long SizeBytes,
    string? RefsHash,
    long CommitCount,
    long FreeBytes,
    long DurationMs);
