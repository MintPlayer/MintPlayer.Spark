namespace CodeCoverage.GitMirror.Mirror;

// The wire shapes of POST /repos/{provider}/{repoId}/fetch (the sidecar contract). JSON camelCase.

public sealed class FetchRequestWire
{
    public string? CloneUrl { get; set; }
    public CredentialWire? Credential { get; set; }
    public QuotaWire? Quota { get; set; }
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
}

public sealed record FetchResponse(
    string Outcome,
    string? Reason,
    long SizeBytes,
    string? RefsHash,
    long CommitCount,
    long FreeBytes,
    long DurationMs);
