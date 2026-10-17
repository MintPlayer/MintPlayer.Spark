using MintPlayer.Spark.Messaging.Abstractions;

namespace CodeCoverage.Forge;

/// <summary>
/// The forge said "not now": a rate limit, primary or secondary (dependency-updates PRD P16, R37). Never a refusal and
/// never an absence: a throttled read must not be mistaken for a missing file (an onboarding built from an empty tree
/// would open a wrong pull request), and a throttled write must not be mistaken for a missing permission.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="RetryAfterException"/>, so a recipient that lets it escape is parked by the message bus for at least
/// <see cref="RetryAfterException.Delay"/> and the park costs a fraction of an attempt (R30), not a whole one.
/// </para>
/// <para>
/// <b>Never thrown bare by an adapter</b> (D-O1): every forge read and write answers a rate limit with
/// <see cref="ForgeRequestException"/> (status 429) carrying this as its inner exception. Every existing caller already
/// treats that as retryable (the sagas' transient retry, the best-effort catches), and the bus looks one level in for a
/// <see cref="RetryAfterException"/>, so a recipient that lets it escape is parked rather than charged. Code that wants
/// the hint uses <see cref="Find"/>.
/// </para>
/// </remarks>
public sealed class ForgeRateLimitedException : RetryAfterException
{
    public ForgeRateLimitedException(TimeSpan retryAfter, DateTimeOffset? resetsAtUtc, EForgeRateLimitKind kind, string message, Exception? inner = null)
        : base(retryAfter, message, inner)
    {
        ResetsAtUtc = resetsAtUtc;
        Kind = kind;
    }

    /// <summary>When the forge said the limit resets, when it said; null for a secondary limit without a hint.</summary>
    public DateTimeOffset? ResetsAtUtc { get; }

    public EForgeRateLimitKind Kind { get; }

    /// <summary>The rate limit behind <paramref name="exception"/>: itself, or one of its inner exceptions (at most three
    /// levels, which covers the wrappers this app adds); null when it is not one.</summary>
    public static ForgeRateLimitedException? Find(Exception? exception)
    {
        for (var depth = 0; exception is not null && depth < 4; depth++, exception = exception.InnerException)
            if (exception is ForgeRateLimitedException limited)
                return limited;
        return null;
    }
}

/// <summary>Which limit was hit (R37).</summary>
public enum EForgeRateLimitKind
{
    /// <summary>The hourly budget is spent: GitHub's 403 or 429 with <c>x-ratelimit-remaining: 0</c>, until <c>x-ratelimit-reset</c>.</summary>
    Primary,

    /// <summary>Too much at once: GitHub's secondary limit, recognised by its message, honouring <c>retry-after</c>, else at least a minute.</summary>
    Secondary,

    /// <summary>A GraphQL answer of HTTP 200 with an <c>errors[].type</c> of <c>RATE_LIMITED</c>.</summary>
    GraphQL,
}
