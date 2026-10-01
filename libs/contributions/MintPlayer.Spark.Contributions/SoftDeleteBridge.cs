using System.Runtime.CompilerServices;
using MintPlayer.Spark.SoftDelete;

namespace MintPlayer.Spark.Contributions;

/// <summary>
/// The only code that names <see cref="ISoftDeletable"/>. SoftDelete is a compile-time reference of this
/// package (not a dependency of its consumers), so these methods are kept out of line and are called
/// only when <see cref="ContributionDescriptor.IsSoftDeletable"/> says the consumer's compilation — and
/// therefore its output — has the SoftDelete abstractions.
/// </summary>
internal static class SoftDeleteBridge
{
    /// <summary>The <c>DeleteReason</c> a withdrawal writes: the author removed the row themselves (PRD Q3).</summary>
    public const string WithdrawnReason = "withdrawn";

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool IsDeleted(object document) => document is ISoftDeletable { IsDeleted: true };

    /// <summary>Whether <paramref name="document"/> was soft-deleted by <paramref name="userId"/> withdrawing it (not hidden by someone else).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool IsWithdrawnBy(object document, string userId)
        => document is ISoftDeletable { IsDeleted: true } d
           && string.Equals(d.DeletedBy, userId, StringComparison.Ordinal)
           && string.Equals(d.DeleteReason, WithdrawnReason, StringComparison.Ordinal);

    /// <summary>The <c>DeleteReason</c> of a contribution a moderator hid by removing its whole version (Delete on the current type).</summary>
    public const string VersionRemovedReason = "version-removed";

    /// <summary>The <c>DeleteReason</c> of a contribution hidden because a moderator reverted its slot to an older one.</summary>
    public const string RevertedReason = "reverted";

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Withdraw(object document, string userId, DateTimeOffset now) => Hide(document, userId, now, WithdrawnReason);

    /// <summary>Soft-deletes <paramref name="document"/> by <paramref name="userId"/> with <paramref name="reason"/>.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Hide(object document, string userId, DateTimeOffset now, string reason)
    {
        var d = (ISoftDeletable)document;
        d.IsDeleted = true;
        d.DeletedAt = now;
        d.DeletedBy = userId;
        d.DeleteReason = reason;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Revive(object document)
    {
        var d = (ISoftDeletable)document;
        d.IsDeleted = false;
        d.DeletedAt = null;
        d.DeletedBy = null;
        d.DeleteReason = null;
    }
}
