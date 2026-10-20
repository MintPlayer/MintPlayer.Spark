using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Consent;
using MintPlayer.Spark.Abstractions.Visitor;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Consent;

/// <summary>
/// Moves a guest's consent decisions onto the account they signed in to (X21: "merged on sign-in like the basket").
/// Per purpose, the <b>newer</b> decision wins: a guest who just refused analytics on the banner is not overruled by an
/// older grant on the account, and the reverse. Both records' histories are kept on the account (the proof); the
/// visitor's record is deleted.
/// </summary>
/// <remarks>Runs inside the visitor merge's cluster-wide transaction, the same kind of session every consent write uses.</remarks>
internal sealed class SparkConsentVisitorMergeHandler(IOptions<SparkConsentOptions> options) : ISparkVisitorMergeHandler
{
    public async Task MergeAsync(string visitorId, string userId, IAsyncDocumentSession session, CancellationToken cancellationToken)
    {
        var visitor = await session.LoadAsync<SparkConsentRecord>(SparkConsentRecord.IdFor(SparkConsentSubject.Visitor(visitorId)), cancellationToken);
        if (visitor is null)
            return;

        var accountId = SparkConsentRecord.IdFor(SparkConsentSubject.Account(userId));
        var account = await session.LoadAsync<SparkConsentRecord>(accountId, cancellationToken);
        var isNew = account is null;
        account ??= new SparkConsentRecord { Id = accountId, SubjectKind = SparkConsentSubjectKind.Account, SubjectId = userId };

        Merge(visitor, account, options.Value.HistoryLimit);

        if (isNew)
            await session.StoreAsync(account, accountId, cancellationToken);
        session.Delete(visitor);
    }

    /// <summary>The merge rule itself, separated for the unit test.</summary>
    internal static void Merge(SparkConsentRecord visitor, SparkConsentRecord account, int historyLimit)
    {
        // Past decisions first, as history only (a loaded record's history holds copies of its current decisions,
        // which Apply adds back below); then the visitor's decisions in force, which win when newer.
        var current = visitor.Decisions.Values.Select(d => (d.Purpose, d.AtUtc, d.Granted)).ToHashSet();
        foreach (var past in visitor.History.Where(h => !current.Contains((h.Purpose, h.AtUtc, h.Granted))))
        {
            account.History.Add(past);
        }
        foreach (var decision in visitor.Decisions.Values)
        {
            account.Apply(decision, historyLimit);
        }
    }
}
