using Microsoft.Extensions.Options;

namespace MintPlayer.Spark.Commerce.Retention;

/// <summary>
/// Commerce's built-in retention floors (§4.15). ⚠️ None of these is legal advice, and each is marked unconfirmed until
/// checked against the official text for the host's country (D37). The EUR-Lex pages could not be fetched from the
/// environment this was written in (2026-10-19); the citations below are to the articles the gap analysis named and
/// must be re-read before release. A host's counsel sets <c>Spark:Commerce:Retention:ConfirmedFloors</c>.
/// </summary>
internal sealed class CommerceRetentionFloors : IRetentionFloorSource
{
    public IEnumerable<RetentionFloor> GetFloors(string country)
    {
        var belgium = string.Equals(country, "BE", StringComparison.OrdinalIgnoreCase);

        // Invoices, credit notes and the accounting records behind them. Belgium: 10 years (VAT Code art. 60 §3 as
        // amended from 2023; Code of Economic Law art. III.86 for accounting documents). Elsewhere the VAT Directive
        // 2006/112/EC Art. 247 leaves the period to the member state: no EU-wide floor is assumed, 10 years is used
        // as the conservative default until the host confirms its own.
        var accounting = TimeSpan.FromDays(3653);
        var accountingBasis = belgium
            ? "Belgian VAT Code art. 60 §3; Code of Economic Law art. III.86 (10 years)"
            : "VAT Directive 2006/112/EC Art. 247 (member-state period; 10 years assumed)";
        yield return new(CommerceDocumentTypes.Invoices, accounting, accountingBasis, country, false);
        yield return new(CommerceDocumentTypes.Orders, accounting, accountingBasis + "; orders back the invoices", country, false);
        yield return new(CommerceDocumentTypes.Payments, accounting, accountingBasis + "; payments and disputes back the accounts", country, false);
        yield return new(CommerceDocumentTypes.MoneyAudit, accounting, accountingBasis + "; the money audit backs the accounts", country, false);
        yield return new(CommerceDocumentTypes.PriceHistory, TimeSpan.FromDays(30),
            "Price Indication Directive 98/6/EC Art. 6a(2): the prior price looks back at least 30 days", null, false);

        // DAC7: records kept at least 5 and at most 10 years after the end of the reportable period (Directive (EU)
        // 2021/514, Annex V). The PRD's "10 years" is the maximum; the floor is the minimum.
        yield return new(CommerceDocumentTypes.Dac7Records, TimeSpan.FromDays(1827),
            "Directive (EU) 2021/514 (DAC7) Annex V: at least 5, at most 10 years after the reportable period", null, false);

        // DSA Art. 30(3): trader data kept for six months after the end of the contractual relationship, then deleted.
        yield return new(CommerceDocumentTypes.TraderData, TimeSpan.FromDays(183),
            "Regulation (EU) 2022/2065 (DSA) Art. 30(3): 6 months after the relationship ends", null, false);

        // No statutory floor found for these; kept for the transparency report period (DSA Art. 15 and 24: yearly).
        yield return new(CommerceDocumentTypes.DsaNotices, TimeSpan.FromDays(366),
            "DSA Art. 15/24: the yearly transparency report needs the year's notices and decisions", null, false);
        yield return new(CommerceDocumentTypes.SellerExports, TimeSpan.FromDays(366),
            "GDPR Art. 5(2) accountability: the export audit trail (no statutory floor; one year assumed)", null, false);
    }
}

/// <summary>
/// The effective retention per document type (§4.15): the host's period, never below the floor. Startup refuses a
/// period below its floor (<see cref="CommerceStartupValidation"/>).
/// </summary>
internal sealed class CommerceRetention(IOptions<SparkCommerceOptions> options, IEnumerable<IRetentionFloorSource> sources) : ICommerceRetention
{
    private readonly Lazy<IReadOnlyDictionary<string, RetentionFloor>> floors = new(() => Build(options.Value, sources));

    public RetentionFloor? GetFloor(string documentType)
        => floors.Value.TryGetValue(documentType, out var floor) ? floor : null;

    public TimeSpan GetPeriod(string documentType)
    {
        var floor = GetFloor(documentType)?.Floor ?? TimeSpan.Zero;
        return options.Value.Retention.Periods.TryGetValue(documentType, out var period) && period > floor ? period : floor;
    }

    /// <summary>The floors in force: the largest per type across sources, then the host's confirmed overrides.</summary>
    internal static IReadOnlyDictionary<string, RetentionFloor> Build(SparkCommerceOptions options, IEnumerable<IRetentionFloorSource> sources)
    {
        var result = new Dictionary<string, RetentionFloor>(StringComparer.OrdinalIgnoreCase);
        foreach (var floor in sources.SelectMany(s => s.GetFloors(options.Country)))
        {
            if (!result.TryGetValue(floor.DocumentType, out var existing) || floor.Floor > existing.Floor)
                result[floor.DocumentType] = floor;
        }
        foreach (var (type, confirmed) in options.Retention.ConfirmedFloors)
            result[type] = new RetentionFloor(type, confirmed, "Confirmed by the host (Spark:Commerce:Retention:ConfirmedFloors)", options.Country, true);
        return result;
    }
}
