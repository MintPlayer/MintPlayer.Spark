using MintPlayer.Spark.Abstractions.Interceptors;

namespace MintPlayer.Spark.Moderation.Services;

/// <summary>
/// Moderation's veto over satellite writes (contributions M5, PRD S-C5): a suspended account writes
/// nothing, and a locked <see cref="IModeratable"/> document (a locked contribution) is not rewritten or
/// withdrawn by those the lock binds. Refuses exactly as a PO save is refused. The interceptor's own
/// checks, so the two answers never drift.
/// </summary>
internal sealed class ModerationSatelliteWriteGuard(ModerationInterceptor interceptor) : ISatelliteWriteGuard
{
    public async ValueTask EnsureMayWriteAsync(SatelliteWriteContext context)
        => await interceptor.EnsureMayWriteSatelliteAsync(context);
}
