using CodeCoverage.Entities;
using Xunit;

namespace CodeCoverage.Tests.Ingestion;

/// <summary>
/// The rolling fork-upload allowance — the app's only accumulating limit.
/// </summary>
/// <remarks>
/// <para>
/// Everything else that looks like a bound in this app is per-request (report count, body size,
/// file-list length) or per-window-of-time (the rate limiters), and none of them add up across
/// requests. That was fine while every upload came from somebody holding a credential for the
/// repository they were filling.
/// </para>
/// <para>
/// ⚠️ The window is the subtle part: an expired one must be spendable <em>whatever count it
/// carries</em>. Reading a stale count as current is what would turn a day's allowance into a
/// permanent ban, and nothing in this app would ever reset it.
/// </para>
/// </remarks>
public class ForkUploadBudgetTests
{
    private static readonly DateTime Now = new(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_repository_that_has_never_taken_one_has_room()
        => Assert.True(ForkUploadBudget.HasRoom(null, Now));

    [Fact]
    public void Accepting_the_first_one_starts_a_window()
    {
        var budget = ForkUploadBudget.Accept(null, Now);

        Assert.Equal(1, budget.Count);
        Assert.Equal(Now, budget.WindowStartedUtc);
    }

    [Fact]
    public void Accepting_within_the_window_increments_without_moving_it()
    {
        var first = ForkUploadBudget.Accept(null, Now);
        var second = ForkUploadBudget.Accept(first, Now.AddHours(3));

        Assert.Equal(2, second.Count);
        // ⚠️ The window must NOT slide forward, or a steady trickle of uploads would keep pushing
        // it and the allowance would never reset.
        Assert.Equal(Now, second.WindowStartedUtc);
    }

    [Fact]
    public void A_full_window_has_no_room()
    {
        var full = new ForkUploadBudget(ForkUploadBudget.PerWindow, Now);

        Assert.False(ForkUploadBudget.HasRoom(full, Now.AddHours(1)));
    }

    [Fact]
    public void One_below_the_limit_still_has_room()
    {
        var nearlyFull = new ForkUploadBudget(ForkUploadBudget.PerWindow - 1, Now);

        Assert.True(ForkUploadBudget.HasRoom(nearlyFull, Now.AddHours(1)));
    }

    /// <summary>
    /// The case that makes this a rolling allowance rather than a permanent cap.
    /// </summary>
    [Fact]
    public void An_expired_window_is_spendable_however_full_it_was()
    {
        var full = new ForkUploadBudget(ForkUploadBudget.PerWindow * 10, Now);
        var later = Now + ForkUploadBudget.Window;

        Assert.True(ForkUploadBudget.HasRoom(full, later));

        var restarted = ForkUploadBudget.Accept(full, later);
        Assert.Equal(1, restarted.Count);
        Assert.Equal(later, restarted.WindowStartedUtc);
    }

    /// <summary>
    /// Exactly at the boundary the window has expired — an off-by-one here would let a full budget
    /// refuse for one extra request, or a fresh one accept one too many.
    /// </summary>
    [Fact]
    public void The_window_expires_at_its_end_not_after_it()
    {
        var full = new ForkUploadBudget(ForkUploadBudget.PerWindow, Now);

        Assert.False(ForkUploadBudget.HasRoom(full, Now + ForkUploadBudget.Window - TimeSpan.FromSeconds(1)));
        Assert.True(ForkUploadBudget.HasRoom(full, Now + ForkUploadBudget.Window));
    }

    /// <summary>
    /// A full window's worth of uploads, one at a time — the sequence the endpoint actually drives,
    /// asserting the limit is reached exactly once rather than approximately.
    /// </summary>
    [Fact]
    public void The_limit_is_reached_after_exactly_the_allowance()
    {
        ForkUploadBudget? budget = null;

        for (var i = 0; i < ForkUploadBudget.PerWindow; i++)
        {
            Assert.True(ForkUploadBudget.HasRoom(budget, Now), $"refused at upload {i + 1}");
            budget = ForkUploadBudget.Accept(budget, Now);
        }

        Assert.False(ForkUploadBudget.HasRoom(budget, Now));
        Assert.Equal(ForkUploadBudget.PerWindow, budget!.Count);
    }
}
