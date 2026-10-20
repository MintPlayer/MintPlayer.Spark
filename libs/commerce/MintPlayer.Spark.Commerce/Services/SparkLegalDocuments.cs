using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Services;

/// <summary>
/// Versioned legal documents (GA2, §4.20). Drafts may be edited; publishing assigns the next version number per
/// (kind, owner) and freezes the document. Queries go through LINQ (parameters), never spliced RQL.
/// </summary>
internal sealed class SparkLegalDocuments(IDocumentStore store, TimeProvider clock) : ISparkLegalDocuments
{
    public async Task<IReadOnlyList<LegalDocumentRef>> GetInForceAsync(string? organizationId, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenAsyncSession();
        var docs = await LoadPublishedAsync(session, organizationId, cancellationToken);
        return InForce(docs, organizationId, at).Select(d => d.ToRef()).ToArray();
    }

    public async Task<IReadOnlyList<string>> GetMissingAsync(string? organizationId, IEnumerable<string> required, CancellationToken cancellationToken = default)
    {
        var inForce = await GetInForceAsync(organizationId, clock.GetUtcNow(), cancellationToken);
        var kinds = inForce.Select(d => d.Kind).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return required.Where(k => !kinds.Contains(k)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task RecordAcceptanceAsync(IAsyncDocumentSession session, string subjectId, LegalDocumentRef document, string source, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        ArgumentNullException.ThrowIfNull(document);
        var id = $"LegalAcceptances/{subjectId}/{document.Id}";
        if (await session.LoadAsync<LegalAcceptance>(id, cancellationToken) is not null)
            return; // accepted before: the first acceptance is the record
        await session.StoreAsync(new LegalAcceptance
        {
            Id = id,
            SubjectId = subjectId,
            DocumentId = document.Id,
            Kind = document.Kind,
            Version = document.Version,
            DocumentOrganizationId = document.OrganizationId,
            AcceptedAt = clock.GetUtcNow(),
            Source = source,
        }, id, cancellationToken);
    }

    /// <summary>Publishes draft <paramref name="documentId"/>: the next version for its (kind, owner), frozen from now.</summary>
    internal async Task<LegalDocumentRef> PublishAsync(IAsyncDocumentSession session, string documentId, string? publishedBy, CancellationToken ct)
    {
        var doc = await session.LoadAsync<LegalDocument>(documentId, ct)
            ?? throw new SparkValidationException("The legal document does not exist.", "Id");
        if (doc.PublishedAt is not null)
            throw new SparkValidationException("This version is already published; create a new draft for a new version.", "Id");
        if (doc.Body is null || doc.Title is null)
            throw new SparkValidationException("A legal document needs a title and a text before it is published.", "Body");

        var kind = doc.Kind;
        var owner = doc.OrganizationId;
        // Non-stale: two publishes in a row must not both read the same latest version.
        var latest = await session.Query<LegalDocument>()
            .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(15)))
            .Where(d => d.Kind == kind && d.OrganizationId == owner && d.PublishedAt != null)
            .OrderByDescending(d => d.Version)
            .Select(d => d.Version)
            .FirstOrDefaultAsync(ct);
        var now = clock.GetUtcNow();
        doc.Version = latest + 1;
        doc.PublishedAt = now;
        doc.PublishedBy = publishedBy;
        if (doc.EffectiveFrom < now) doc.EffectiveFrom = now;
        return doc.ToRef();
    }

    /// <summary>Saves a draft, refusing any change to a published version (a new version is a new draft).</summary>
    internal static void EnsureEditable(LegalDocument existing)
    {
        if (existing.PublishedAt is not null)
            throw new SparkValidationException("A published legal document is never edited; create a new version.", "Id");
    }

    private static async Task<List<LegalDocument>> LoadPublishedAsync(IAsyncDocumentSession session, string? organizationId, CancellationToken ct)
    {
        // Two simple queries (the seller's and the platform's), parameters only.
        var platform = await session.Query<LegalDocument>().Where(d => d.OrganizationId == null && d.PublishedAt != null).Take(1024).ToListAsync(ct);
        if (organizationId is null) return platform;
        var own = await session.Query<LegalDocument>().Where(d => d.OrganizationId == organizationId && d.PublishedAt != null).Take(1024).ToListAsync(ct);
        return [.. own, .. platform];
    }

    /// <summary>
    /// Pure: per kind, the highest published version in force at <paramref name="at"/>, the seller's own first, the
    /// platform's otherwise. For tests.
    /// </summary>
    internal static IEnumerable<LegalDocument> InForce(IEnumerable<LegalDocument> published, string? organizationId, DateTimeOffset at)
        => published
            .Where(d => d.PublishedAt is not null && d.EffectiveFrom <= at)
            .GroupBy(d => d.Kind, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Where(d => organizationId is not null && d.OrganizationId == organizationId).MaxBy(d => d.Version)
                         ?? g.Where(d => d.OrganizationId is null).MaxBy(d => d.Version))
            .Where(d => d is not null)
            .Select(d => d!);
}
