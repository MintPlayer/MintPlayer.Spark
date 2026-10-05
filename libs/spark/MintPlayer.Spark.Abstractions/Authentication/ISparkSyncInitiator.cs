namespace MintPlayer.Spark.Abstractions.Authentication;

/// <summary>
/// The user a module sync write was made for, while the owner module applies it. Scoped: set by the
/// sync apply for the duration of one write, <see langword="null"/> otherwise.
/// </summary>
/// <remarks>
/// <para>
/// A write a replica forwards (<c>PersistentObjectOperation.Sync</c>) arrives under the sending
/// module's certificate, so <see cref="ISparkCurrentUser.Id"/> is <see langword="null"/> on the owner.
/// The replica states which user made the edit (<c>SyncAction.InitiatorId</c>), and this is how the
/// owner's stamping interceptors learn it (#271, F2) — without it the owner kept the previous
/// <c>ModifiedBy</c>, and replication then copied that stale value back over the replica's own stamp.
/// </para>
/// <para>
/// ⚠️ The id is asserted by an authenticated module, the same trust its data already carries. It is
/// used for stamping only, never for authorization.
/// </para>
/// </remarks>
public interface ISparkSyncInitiator
{
    /// <summary>
    /// The user id the replica stated for the sync write being applied, or <see langword="null"/> when
    /// no sync write is being applied, or the replica stated none (an anonymous or background write,
    /// or a replica older than #271).
    /// </summary>
    string? UserId { get; }
}
