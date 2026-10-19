using System.Security.Cryptography;
using System.Text;
using MintPlayer.Spark.Abstractions.Consent;

namespace MintPlayer.Spark.Consent;

/// <summary>
/// One subject's consent decisions (X21): the current decision per purpose and the history of every decision, kept as
/// the proof GDPR Art. 7(1) asks for. Written only through cluster-wide sessions, so the sign-in merge (which runs in
/// the visitor merge's cluster-wide transaction) and a direct write never race past each other.
/// </summary>
/// <remarks>
/// Ids are natural (<see cref="IdFor"/>). An e-mail subject's id holds a SHA-256 of the normalised address, never the
/// address itself; the address is a field, so the export can show it and erasure can find it.
/// </remarks>
public sealed class SparkConsentRecord
{
    public string? Id { get; set; }

    public SparkConsentSubjectKind SubjectKind { get; set; }

    /// <summary>The account or visitor id; null for an e-mail subject.</summary>
    public string? SubjectId { get; set; }

    /// <summary>The normalised address of an e-mail subject.</summary>
    public string? Email { get; set; }

    /// <summary>Purpose name → the decision in force.</summary>
    public Dictionary<string, SparkConsentDecision> Decisions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every decision, oldest first (capped at <see cref="SparkConsentOptions.HistoryLimit"/>).</summary>
    public List<SparkConsentDecision> History { get; set; } = [];

    /// <summary>The document id of <paramref name="subject"/>'s record.</summary>
    public static string IdFor(SparkConsentSubject subject) => subject.Kind switch
    {
        SparkConsentSubjectKind.Account => "SparkConsentRecords/account/" + subject.Id,
        SparkConsentSubjectKind.Visitor => "SparkConsentRecords/visitor/" + subject.Id,
        _ => "SparkConsentRecords/email/" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(subject.Id))),
    };

    /// <summary>Applies <paramref name="decision"/> when it is newer than the one in force (or there is none).</summary>
    internal bool Apply(SparkConsentDecision decision, int historyLimit)
    {
        if (Decisions.TryGetValue(decision.Purpose, out var current) && current.AtUtc > decision.AtUtc)
        {
            AppendHistory(decision, historyLimit);
            return false;
        }

        Decisions[decision.Purpose] = decision;
        AppendHistory(decision, historyLimit);
        return true;
    }

    private void AppendHistory(SparkConsentDecision decision, int historyLimit)
    {
        History.Add(decision);
        History.Sort((a, b) => a.AtUtc.CompareTo(b.AtUtc));
        var excess = History.Count - Math.Max(1, historyLimit);
        if (excess > 0)
            History.RemoveRange(0, excess);
    }
}

/// <summary>One decision, with its proof: the policy version, when, where, in which language, and a hash of the texts shown.</summary>
public sealed class SparkConsentDecision
{
    public string Purpose { get; set; } = string.Empty;

    public bool Granted { get; set; }

    public string PolicyVersion { get; set; } = string.Empty;

    public DateTimeOffset AtUtc { get; set; }

    /// <summary>A <see cref="SparkConsentSources"/> value.</summary>
    public string Source { get; set; } = string.Empty;

    public string? Language { get; set; }

    /// <summary>SHA-256 (hex) of the purpose's title, description and vendors in <see cref="Language"/>, and the policy version.</summary>
    public string? TextHash { get; set; }
}
