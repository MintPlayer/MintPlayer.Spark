using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MintPlayer.Spark.Replication.Abstractions.Rendering;

// ⚠️ Linked into MintPlayer.Spark.LibraryGenerators (netstandard2.0) with the rest of Rendering/ (the renderer uses
// it). Keep it free of records, ranges and Span-only APIs.

/// <summary>
/// One per-consumer row condition (warehouse §4.2.3, "Row filter"): a source document reaches the consumer only when
/// its top-level member <see cref="Field"/> holds one of <see cref="AllowedValues"/> (ordinal comparison). A missing
/// or null member, or a non-string one, never matches. No values: no row matches.
/// </summary>
public sealed class ReplicaRowCondition
{
    private readonly string[] allowed;

    /// <exception cref="ArgumentException"><paramref name="field"/> is not a plain identifier.</exception>
    public ReplicaRowCondition(string field, IEnumerable<string> allowedValues)
    {
        if (!ReplicaScriptRenderer.IsIdentifier(field))
            throw new ArgumentException($"The row-filter field '{field}' must be a plain identifier.", nameof(field));
        if (allowedValues is null)
            throw new ArgumentNullException(nameof(allowedValues));

        Field = field;
        allowed = allowedValues.Where(v => v is not null).Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal).ToArray();
    }

    /// <summary>The source document's top-level member the condition reads (e.g. <c>OwnerId</c>).</summary>
    public string Field { get; }

    /// <summary>The values a row may hold, distinct and in ordinal order.</summary>
    public IReadOnlyList<string> AllowedValues => allowed;

    public bool Admits(string? value)
        => value is not null && Array.BinarySearch(allowed, value, StringComparer.Ordinal) >= 0;
}

/// <summary>
/// What the owner lets one consumer receive of one source collection: every condition must admit a row. Built by the
/// owner from its <c>IReplicaRowFilter</c>s at delivery time, never sent by the consumer, and applied by both
/// transports: the HTTP push and reconcile skip rows it refuses, and the owner renders it into the ETL script.
/// </summary>
public sealed class ReplicaRowScope
{
    public ReplicaRowScope(IEnumerable<ReplicaRowCondition> conditions)
    {
        if (conditions is null)
            throw new ArgumentNullException(nameof(conditions));
        Conditions = conditions.OrderBy(c => c.Field, StringComparer.Ordinal).ToArray();
        if (Conditions.Count == 0)
            throw new ArgumentException("A row scope needs at least one condition; no scope is null.", nameof(conditions));
    }

    public IReadOnlyList<ReplicaRowCondition> Conditions { get; }

    /// <summary>The scope from the conditions that apply, or <see langword="null"/> when none does (no restriction).</summary>
    public static ReplicaRowScope? Combine(IEnumerable<ReplicaRowCondition?> conditions)
    {
        var applying = conditions.Where(c => c is not null).Select(c => c!).ToList();
        return applying.Count == 0 ? null : new ReplicaRowScope(applying);
    }

    /// <summary>Whether a row with <paramref name="read"/> for each condition's field reaches the consumer.</summary>
    public bool Admits(Func<string, string?> read)
    {
        if (read is null)
            throw new ArgumentNullException(nameof(read));
        foreach (var condition in Conditions)
        {
            if (!condition.Admits(read(condition.Field)))
                return false;
        }
        return true;
    }

    /// <summary>
    /// A canonical text of the scope, stored with a deployment so a changed scope redeploys (ETL) or reconciles (HTTP).
    /// </summary>
    public string Fingerprint
    {
        get
        {
            var text = new StringBuilder();
            foreach (var condition in Conditions)
            {
                text.Append(condition.Field).Append('=').Append('[');
                for (var i = 0; i < condition.AllowedValues.Count; i++)
                {
                    if (i > 0)
                        text.Append(',');
                    text.Append(ReplicaScriptRenderer.JavaScriptString(condition.AllowedValues[i]));
                }
                text.Append("];");
            }
            return ReplicaScriptRenderer.Hash(text.ToString(), "row-scope");
        }
    }
}
