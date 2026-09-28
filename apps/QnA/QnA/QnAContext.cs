using MintPlayer.Spark;
using QnA.Entities;
using Raven.Client.Documents.Linq;

namespace QnA;

/// <summary>
/// QnA's two collections. Everything else the demo shows — votes, reputation, flags, locks, the
/// review queue — is Moderation's own documents behind <c>/spark/moderation/*</c> (T6), not
/// persistent objects this context declares.
/// </summary>
public class QnAContext : SparkContext
{
    public IRavenQueryable<Question> Questions => Session.Query<Question>();
    public IRavenQueryable<Answer> Answers => Session.Query<Answer>();
}
