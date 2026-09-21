namespace CodeCoverage.Entities;

/// <summary>
/// A rolling allowance of fork-contributed uploads for one repository.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>This is the app's only accumulating limit.</b> Everything else that looks like a bound is
/// either per-request (report count, body size, file-list length) or per-window-of-time (the rate
/// limiters), and none of them add up across requests. That was acceptable while every upload came
/// from somebody holding a credential for the repository they were filling; it stopped being
/// acceptable when an anonymous endpoint could write to a repository on a stranger's behalf.
/// </para>
/// <para>
/// <b>A window rather than a running total</b>, because a running total would eventually refuse a
/// repository that has done nothing wrong for a year, and nothing in this app would ever reset it.
/// The window is self-cleaning: the first request after it expires starts a new one, so there is no
/// sweeper to write, nothing to schedule, and no state that can be left stale by a deploy.
/// </para>
/// </remarks>
/// <param name="Count">Fork uploads accepted since <paramref name="WindowStartedUtc"/>.</param>
/// <param name="WindowStartedUtc">When the current window began.</param>
public sealed record ForkUploadBudget(int Count, DateTime WindowStartedUtc)
{
    /// <summary>How long a window lasts.</summary>
    /// <remarks>
    /// A day, so the allowance reads as "per repository per day" — the unit a maintainer would
    /// reason in when asked how much a fork can cost them.
    /// </remarks>
    public static readonly TimeSpan Window = TimeSpan.FromDays(1);

    /// <summary>
    /// Fork uploads one repository may accept per <see cref="Window"/>.
    /// </summary>
    /// <remarks>
    /// ⚠️ Sized for the honest case with room to spare, not for an adversary: a busy public
    /// repository might see a dozen fork pull requests in a day, each uploading once per CI job.
    /// It bounds the damage rather than preventing abuse — the rate limiter is what makes abuse
    /// slow, and this is what makes it finite.
    /// </remarks>
    public const int PerWindow = 200;

    /// <summary>
    /// The budget after accepting one more upload at <paramref name="now"/>, starting a fresh
    /// window if the current one has expired.
    /// </summary>
    public static ForkUploadBudget Accept(ForkUploadBudget? current, DateTime now)
        => current is { } budget && now - budget.WindowStartedUtc < Window
            ? budget with { Count = budget.Count + 1 }
            : new ForkUploadBudget(1, now);

    /// <summary>
    /// Whether one more fork upload may be accepted at <paramref name="now"/>.
    /// </summary>
    /// <remarks>
    /// An expired window is always spendable, whatever count it carries — the count belongs to a
    /// window that is over, and reading it as current is what would make the limit permanent.
    /// </remarks>
    public static bool HasRoom(ForkUploadBudget? current, DateTime now)
        => current is not { } budget
            || now - budget.WindowStartedUtc >= Window
            || budget.Count < PerWindow;
}
