using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.ReferenceData;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Commerce.Invoicing.Reference;

/// <summary>A TEDB rate suggestion (GC10), shown beside the host's own rates. <b>Never applied automatically</b> (§4.8).</summary>
/// <param name="Country">ISO country.</param>
/// <param name="Type">TEDB rate type (<c>STANDARD</c>, <c>REDUCED</c>).</param>
/// <param name="RateBasisPoints">The rate (21 % = 2100).</param>
/// <param name="Category">TEDB category id, or <c>standard</c>.</param>
/// <param name="CategoryDescription">TEDB's description.</param>
/// <param name="SituationOn">The date TEDB stated the rate for.</param>
public sealed record VatRateSuggestionItem(string Country, string Type, int RateBasisPoints, string Category, string? CategoryDescription, DateOnly SituationOn);

/// <summary>Reference data the VAT rules may use: territories (GA4) and TEDB rate suggestions (GC10). Optional.</summary>
public interface IVatReferenceSource
{
    Task<IReadOnlyList<VatTerritoryOptions>> GetTerritoriesAsync(string country, CancellationToken cancellationToken);

    Task<IReadOnlyList<VatRateSuggestionItem>> GetRateSuggestionsAsync(string? country, CancellationToken cancellationToken);
}

/// <summary>No reference data: only the host's configured territories, no suggestions.</summary>
internal sealed class NoVatReferenceSource : IVatReferenceSource
{
    public Task<IReadOnlyList<VatTerritoryOptions>> GetTerritoriesAsync(string country, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<VatTerritoryOptions>>([]);

    public Task<IReadOnlyList<VatRateSuggestionItem>> GetRateSuggestionsAsync(string? country, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<VatRateSuggestionItem>>([]);
}

/// <summary>
/// The reference data the reference-data client syncs into this application's database (<c>AddReferenceDataClient</c>,
/// collections <c>RefCountries</c> and <c>VatRateSuggestions</c>): the country profiles' VAT territories and the
/// Commission's TEDB rates. Registered by <c>UseReferenceDataVat()</c>; without a synced release it answers nothing.
/// Reads by id (countries) or a one-field dynamic query (suggestions). Values only through LINQ parameters.
/// </summary>
internal sealed class SyncedReferenceDataVatSource(IDocumentStore store) : IVatReferenceSource
{
    public async Task<IReadOnlyList<VatTerritoryOptions>> GetTerritoriesAsync(string country, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        RefCountry? doc;
        try { doc = await session.LoadAsync<RefCountry>(RefIds.CountryId(country), cancellationToken); }
        catch (ArgumentException) { return []; }
        if (doc is null)
            return [];
        return doc.Profile.VatTerritories.Select(t => new VatTerritoryOptions
        {
            Country = doc.Code,
            Code = t.Code,
            PostcodePattern = t.PostcodePattern,
            OutsideVatTerritory = true,
            LegalBasis = t.LegalBasis,
        }).ToList();
    }

    public async Task<IReadOnlyList<VatRateSuggestionItem>> GetRateSuggestionsAsync(string? country, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        IQueryable<VatRateSuggestion> query = session.Query<VatRateSuggestion>();
        if (country is { Length: 2 })
        {
            var cc = country.ToUpperInvariant();
            query = query.Where(s => s.Country == cc);
        }
        var rows = await query.Take(1024).ToListAsync(cancellationToken);
        // Only the latest situation per country, type and category.
        return rows
            .GroupBy(r => (r.Country, r.Type, r.Category))
            .Select(g => g.OrderByDescending(r => r.SituationOn).First())
            .OrderBy(r => r.Country, StringComparer.Ordinal).ThenBy(r => r.Type, StringComparer.Ordinal).ThenBy(r => r.Category, StringComparer.Ordinal)
            .Select(r => new VatRateSuggestionItem(r.Country, r.Type, r.RateBasisPoints, r.Category, r.CategoryDescription, r.SituationOn))
            .ToList();
    }
}

/// <summary>The VAT territories in force: the host's list wins over the reference data's for the same code.</summary>
internal sealed class VatTerritories(IOptionsMonitor<SparkCommerceInvoicingOptions> options, IVatReferenceSource reference)
{
    /// <summary>The territory a postcode of <paramref name="country"/> falls in, or null.</summary>
    public async Task<VatTerritoryOptions?> FindAsync(string? country, string? postalCode, CancellationToken cancellationToken)
    {
        if (country is not { Length: 2 } || postalCode is not { Length: > 0 })
            return null;
        var configured = options.CurrentValue.Territories.Where(t => string.Equals(t.Country, country, StringComparison.OrdinalIgnoreCase)).ToList();
        var fromReference = await reference.GetTerritoriesAsync(country.ToUpperInvariant(), cancellationToken);
        var all = configured.Concat(fromReference.Where(r => !configured.Any(c => string.Equals(c.Code, r.Code, StringComparison.OrdinalIgnoreCase))));
        var compact = postalCode.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        foreach (var territory in all)
        {
            if (territory.PostcodePattern is not { Length: > 0 } pattern)
                continue;
            try
            {
                if (Regex.IsMatch(compact, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                    return territory;
            }
            catch (ArgumentException) { }
            catch (RegexMatchTimeoutException) { }
        }
        return null;
    }
}
