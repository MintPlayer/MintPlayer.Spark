using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Logging.Models;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Logging.Actions;

/// <summary>
/// Who may read a log (#660 G15). A log that belongs to a record (<see cref="Log.RelatedPersistentObjectId"/>) opens
/// only for a caller who may open that record, through the same gates its own page applies; a missing or expired record
/// refuses it. A log without one follows the <c>Read/Log</c> right alone. The library's <c>security.json</c> grants
/// nothing: an application opts in.
/// <para>
/// ⚠️ <b>The list rows pass the same gate.</b> A <c>Read</c> grant implies <c>Query</c> in Spark
/// (<c>libs/spark/MintPlayer.Spark/AGENTS.md</c>), so <c>Read/Log</c> alone opens <c>Custom.Logs</c>; gating only
/// <c>Read</c> would have listed every log of every record to whoever may open one (#660, found by
/// <c>LogAccessTests</c>). An application that wants the list for admins only refuses <c>Query</c> rows in its own
/// <c>LogActions</c>.
/// </para>
/// <para>
/// Derive from this class under the same name to narrow it further (<c>docs/guide-library-layers.md</c>, "Overriding a
/// library's actions class"), and call <c>base</c> to keep this rule.
/// </para>
/// </summary>
public partial class LogActions : DefaultPersistentObjectActions<Log>
{
    [Inject] private readonly ISparkSession session;
    [Inject] private readonly ISparkReadAccess readAccess;

    public override async Task<bool> IsAllowedAsync(string action, Log entity)
        => action is not ("Read" or "Query")
            || entity.RelatedPersistentObjectId is not { Length: > 0 } related
            || await readAccess.MayReadAsync(related);

    /// <summary>
    /// Every log: the list behind <c>Custom.Logs</c>. It declares no sort: a log's id is a key, not a time, and a log has
    /// no attribute that orders it (<c>--spark-verify-model</c> refuses a sort column off the query surface).
    /// </summary>
    public IQueryable<Log> Logs() => session.Query<Log>();
}
