using System.Text.Json;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Exceptions;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Services;

/// <summary>
/// The approver's half of a diverted save (commerce X7, S10): the proposal's wire JSON is saved again, as the
/// caller, through <see cref="IPersistentObjectSavePath"/> — the sequence of <c>/spark/po/create</c> and
/// <c>/spark/po/update</c>, validation included — with <c>Etag = base</c>, inside one
/// <see cref="ISparkWriteUnit"/> commit with the applier's own writes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the internal save path</b> and not <c>IDatabaseAccess.SavePersistentObjectAsync</c>: it asks for the
/// validation (<c>ISaveValidation.Request</c>), which the public save skips, and it checks the references
/// (a target deleted since the proposal is refused).
/// </para>
/// <para>
/// <b>Same result as proposed.</b> The approver's write shield would silently drop an attribute the approver may
/// not write, so the change applied would be smaller than the one approved. The apply first runs every check on a
/// copy and refuses (<see cref="ReplacedSaveOutcomeKind.Unwritable"/>) when the shield dropped any attribute the
/// proposal sets — the check MCP's <c>apply_changes</c> makes for the same reason.
/// </para>
/// </remarks>
[Register(typeof(ISparkReplacedSaves), ServiceLifetime.Scoped)]
internal sealed partial class SparkReplacedSaves : ISparkReplacedSaves
{
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IPersistentObjectSavePath savePath;
    [Inject] private readonly ISparkWriteUnit writeUnit;
    [Inject] private readonly IAsyncDocumentSession session;

    /// <summary>
    /// The wire form a diverted save is kept in: System.Text.Json with the web defaults an HTTP update binds with,
    /// so the AsDetail converter splits row attributes exactly as it does for a posted object (S10). Not the HTTP
    /// options themselves: those carry the response boundary net, which refuses an object never presented.
    /// </summary>
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);

    /// <summary>A request's budget: the apply's own reads and writes, plus what the interceptors and the applier add.</summary>
    private const int RequestBudget = 60;

    internal static string Serialize(PersistentObject persistentObject) => JsonSerializer.Serialize(persistentObject, WireJson);

    internal static PersistentObject Deserialize(string json)
        => JsonSerializer.Deserialize<PersistentObject>(json, WireJson)
           ?? throw new ArgumentException("The diverted save holds no persistent object.", nameof(json));

    public async Task<ReplacedSaveOutcome> ApplyAsync(ReplacedSave save, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(save);
        var entityType = modelLoader.GetEntityType(save.ObjectTypeId)
            ?? throw new ArgumentException($"Could not find EntityType with ID '{save.ObjectTypeId}'.", nameof(save));

        var proposed = Deserialize(save.PersistentObjectJson);
        var isCreate = string.IsNullOrEmpty(proposed.Id);
        if (!isCreate && string.IsNullOrEmpty(save.BaseEtag))
            throw new ArgumentException("A diverted edit must carry the base version it was made against.", nameof(save));

        try
        {
            // Every check of the apply, on a copy, before anything is written: the version (a stale base is the
            // 409 here already), the rights, the row gates, the validation — and what the shield leaves.
            var check = Deserialize(save.PersistentObjectJson);
            check.Etag = save.BaseEtag;
            if (isCreate)
                await savePath.CheckCreateAsync(entityType, check, cancellationToken);
            else
                await savePath.CheckUpdateAsync(entityType, proposed.Id!, check, cancellationToken);

            // An edit counts only what it changed: an untouched attribute the shield drops keeps its stored value,
            // which is what the proposal showed. A creation counts everything it sets.
            var kept = AttributePaths(check);
            var dropped = AttributePaths(proposed, changedOnly: !isCreate).Where(path => !kept.Contains(path)).ToList();
            if (dropped.Count > 0)
                return new ReplacedSaveOutcome { Kind = ReplacedSaveOutcomeKind.Unwritable, Attributes = dropped };

            var target = Deserialize(save.PersistentObjectJson);
            target.Etag = save.BaseEtag;
            PersistentObject? applied = null;
            using (writeUnit.ApplyingReplacedSave(target, save.OnBehalfOf))
            {
                await writeUnit.RunInOneCommitAsync(async () =>
                {
                    var outcome = isCreate
                        ? await savePath.CreateAsync(entityType, target, target.Parent, cancellationToken)
                        : await savePath.UpdateAsync(entityType, proposed.Id!, target, retry: null, cancellationToken);

                    // Cancelled: nothing of the apply may commit, the applier's own writes included.
                    if (outcome.Kind != SaveOutcomeKind.Saved)
                        throw new SparkCancelException();

                    applied = outcome.Object;
                    if (save.Alongside is { } alongside)
                        await alongside(session);
                }, RequestBudget, $"Spark replaced-save apply ({entityType.Name})");
            }

            return new ReplacedSaveOutcome { Kind = ReplacedSaveOutcomeKind.Applied, Object = applied };
        }
        catch (SparkCancelException)
        {
            return new ReplacedSaveOutcome { Kind = ReplacedSaveOutcomeKind.Cancelled };
        }
        catch (SparkConcurrencyException ex)
        {
            // Changed or deleted since the proposal, or (a creation) its natural id is taken: the proposal is stale.
            return new ReplacedSaveOutcome { Kind = ReplacedSaveOutcomeKind.Conflict, ConflictReason = ex.Reason };
        }
        catch (SparkSaveValidationException ex)
        {
            return new ReplacedSaveOutcome { Kind = ReplacedSaveOutcomeKind.Invalid, Errors = ex.Result.Errors };
        }
    }

    /// <summary>
    /// Every attribute <paramref name="persistentObject"/> carries, as the paths the write shield reports:
    /// <c>Title</c>, and <c>Lines.Text</c> for an attribute of an AsDetail row (any row). With
    /// <paramref name="changedOnly"/>, only the top-level attributes the client marked changed (an AsDetail one with
    /// all of its rows' attributes).
    /// </summary>
    internal static HashSet<string> AttributePaths(PersistentObject persistentObject, bool changedOnly = false)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Collect(persistentObject, prefix: string.Empty, depth: 0);
        return paths;

        void Collect(PersistentObject po, string prefix, int depth)
        {
            // AsDetail nests; the shield stops at the same depth.
            if (depth > 8)
                return;
            foreach (var attribute in po.Attributes)
            {
                if (changedOnly && depth == 0 && !attribute.IsValueChanged)
                    continue;
                paths.Add(prefix + attribute.Name);
                if (attribute is not PersistentObjectAttributeAsDetail asDetail)
                    continue;
                var rowPrefix = prefix + attribute.Name + ".";
                if (asDetail.Object is { } row)
                    Collect(row, rowPrefix, depth + 1);
                foreach (var each in asDetail.Objects ?? [])
                {
                    if (each is not null)
                        Collect(each, rowPrefix, depth + 1);
                }
            }
        }
    }
}
