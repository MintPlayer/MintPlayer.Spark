using MintPlayer.Spark.ReferenceData.Sources;

namespace MintPlayer.Spark.ReferenceData.Build;

/// <summary>
/// The id ledger (PRD §4.3, RD-DEF 12): an append-only map from every source identity to the id it minted.
/// <list type="bullet">
/// <item><b>Mint once.</b> An id is minted the first time a unit is seen and never re-derived, reused or deleted.</item>
/// <item>Every rule-derived id is checked against the ledger. <b>A conflict stops the import of that source</b>
/// (fail closed): a source rewriting history, a code reassigned to another unit, a name match turning ambiguous.</item>
/// <item>The shipped lines come with the adapter package, so independent instances mint the same ids; lines first
/// seen by this instance are <see cref="Added"/> and kept in its local ledger until an adapter release absorbs them (Q-R5).</item>
/// </list>
/// </summary>
public sealed class RefLedger
{
    // Keyed by (country of the id, identity): a source identity is unique within its country's code spaces, and
    // adapters share generic schemes ("postcode:7000" is both an Austrian and a Swiss postal code).
    private readonly Dictionary<string, LedgerEntry> byIdentity = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<LedgerEntry>> byBareIdentity = new(StringComparer.OrdinalIgnoreCase);

    private static string KeyOf(string identity, string id) => (RefIds.CountryOf(id) ?? "_") + "|" + identity;

    private void Index(LedgerEntry entry)
    {
        byIdentity[KeyOf(entry.Identity, entry.Id)] = entry;
        if (!byBareIdentity.TryGetValue(entry.Identity, out var list)) byBareIdentity[entry.Identity] = list = [];
        list.Add(entry);
    }
    private readonly Dictionary<string, LedgerEntry> byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LedgerEntry> added = [];
    private readonly List<LedgerConflict> conflicts = [];

    public RefLedger(IEnumerable<LedgerEntry> known)
    {
        foreach (var entry in known)
        {
            if (byIdentity.TryGetValue(KeyOf(entry.Identity, entry.Id), out var existing))
            {
                if (!string.Equals(existing.Id, entry.Id, StringComparison.OrdinalIgnoreCase))
                    conflicts.Add(new(entry.Source, entry.Identity, existing.Id, entry.Id, "the ledger itself maps one identity to two ids"));
                continue;
            }
            Index(entry);
            byId.TryAdd(entry.Id, entry);
        }
    }

    /// <summary>The lines minted during this build, in mint order.</summary>
    public IReadOnlyList<LedgerEntry> Added => added;

    public IReadOnlyList<LedgerConflict> Conflicts => conflicts;

    /// <summary>Every line, shipped and added, sorted by identity (the committed file's order).</summary>
    public IEnumerable<LedgerEntry> All => byIdentity.Values.OrderBy(e => e.Identity, StringComparer.Ordinal);

    /// <summary>
    /// The id of <paramref name="identity"/>: the ledger's when known, else <paramref name="ruleId"/>, appended. A
    /// disagreement is recorded as a conflict and the ledger's id is returned, so mapping continues and every
    /// conflict of the source is reported at once; the importer then refuses the source.
    /// </summary>
    public string Mint(string source, string identity, string ruleId)
    {
        if (byIdentity.TryGetValue(KeyOf(identity, ruleId), out var known))
        {
            if (!string.Equals(known.Id, ruleId, StringComparison.OrdinalIgnoreCase))
                conflicts.Add(new(source, identity, known.Id, ruleId, "the rule derives another id than the ledger holds"));
            return known.Id;
        }
        if (byId.TryGetValue(ruleId, out var holder))
        {
            conflicts.Add(new(source, identity, holder.Id, ruleId, $"the id already belongs to {holder.Identity} (a code reassigned to a different unit needs an @date suffix)"));
            return ruleId;
        }
        var entry = new LedgerEntry(identity, ruleId, source);
        Index(entry);
        byId[ruleId] = entry;
        added.Add(entry);
        return ruleId;
    }

    /// <summary>
    /// Records that <paramref name=identity/> names the existing <paramref name=id/>: a recode, where a new code keeps
    /// the id the old code minted (§4.3 "a recode adds a dated code; the id keeps the old code"). Checked against the
    /// ledger like <see cref="Mint"/>, without the one-identity-per-id rule.
    /// </summary>
    public string Alias(string source, string identity, string id)
    {
        if (byIdentity.TryGetValue(KeyOf(identity, id), out var known))
        {
            if (!string.Equals(known.Id, id, StringComparison.OrdinalIgnoreCase))
                conflicts.Add(new(source, identity, known.Id, id, "the ledger maps this identity to another id"));
            return known.Id;
        }
        var entry = new LedgerEntry(identity, id, source);
        Index(entry);
        added.Add(entry);
        return id;
    }

    /// <summary>The id a known identity minted, or <see langword="null"/> (lookups that must not mint).</summary>
    /// <remarks>An identity known in several countries is ambiguous here: <see langword="null"/>; pass the country.</remarks>
    public string? Find(string identity, string? country = null)
        => byBareIdentity.TryGetValue(identity, out var entries)
            ? (country is null ? (entries.Count == 1 ? entries[0].Id : null)
                : entries.FirstOrDefault(e => string.Equals(RefIds.CountryOf(e.Id), country, StringComparison.OrdinalIgnoreCase))?.Id)
            : null;
}

/// <summary>A ledger conflict: shown on the import run; the source's import stops.</summary>
public sealed record LedgerConflict(string Source, string Identity, string LedgerId, string RuleId, string Reason)
{
    public override string ToString() => $"{Source}: {Identity} — ledger {LedgerId}, rule {RuleId}: {Reason}";
}

/// <summary>Thrown when a build holds ledger conflicts: nothing of the affected sources is written.</summary>
public sealed class LedgerConflictException(IReadOnlyList<LedgerConflict> conflicts)
    : Exception("The id ledger refused the import:" + Environment.NewLine + string.Join(Environment.NewLine, conflicts.Select(c => "  - " + c)))
{
    public IReadOnlyList<LedgerConflict> Conflicts { get; } = conflicts;
}
