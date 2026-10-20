using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Invoicing.EInvoicing;
using MintPlayer.Spark.Commerce.Invoicing.Models;
using MintPlayer.Spark.Commerce.Invoicing.Services;
using MintPlayer.Spark.Commerce.Invoicing.Vat;
using MintPlayer.Spark.Commerce.Models;
using MintPlayer.Spark.Commerce.Payments.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Commerce.Invoicing.Exports;

/// <summary>A file an export produces.</summary>
public sealed record InvoicingExportFile(string FileName, string ContentType, byte[] Content);

/// <summary>One row of the OSS/IOSS return (Art. 369g / 369t): per member state of consumption, supply type and rate.</summary>
public sealed record OssReturnRow(string MemberState, string SupplyType, int RateBasisPoints, long TaxableMinorUnits, long VatMinorUnits, string Currency, string? CorrectionOfPeriod);

/// <summary>One seller's DAC7 figures for a year (GA6, Directive (EU) 2021/514 Annex V Section III B).</summary>
public sealed record Dac7SellerRow(
    string OrganizationId,
    string Name,
    bool IsEntity,
    string? Tin,
    string? TinCountry,
    string? VatId,
    DateOnly? BirthDate,
    string? Address,
    string? BusinessRegistrationNumber,
    string? Iban,
    string? AccountHolder,
    long[] ConsiderationPerQuarter,
    int[] ActivitiesPerQuarter,
    long[] FeesPerQuarter,
    string Currency,
    bool Excluded,
    bool TicketSalesFlagged,
    IReadOnlyList<string> MissingData);

/// <summary>
/// The periodic exports of §4.8: the Union OSS and IOSS returns (GA4, GC8), the DAC7 report (GA6) and the accounting
/// export (GB4). All read issued documents only, so every figure traces back to an invoice or credit note number.
/// Exports are files for the seller or the platform to file themselves; nothing is submitted to an authority.
/// </summary>
internal sealed class InvoicingExports(
    IDocumentStore store,
    IOptionsMonitor<SparkCommerceInvoicingOptions> options,
    IOptionsMonitor<SparkCommerceOptions> commerce,
    TimeProvider time)
{
    // ---- OSS / IOSS (GA4, GC8) --------------------------------------------------------------------------------------

    /// <summary>
    /// The Union OSS return of an issuer for a quarter (Art. 369f: quarterly; Art. 369g: per member state of consumption,
    /// the taxable amount and VAT per rate). B2C supplies taxed in a member state other than the issuer's: distance
    /// sales of goods (Art. 33(a)) and services such as admission (Art. 54) there. Credit notes on an earlier quarter are
    /// corrections of that quarter (Implementing Regulation (EU) No 282/2011 Art. 61: made in a later return), listed
    /// on their own rows with <c>CorrectionOfPeriod</c>.
    /// </summary>
    public async Task<IReadOnlyList<OssReturnRow>> OssReturnAsync(string issuerKey, int year, int quarter, bool ioss, CancellationToken cancellationToken)
    {
        if (quarter is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(quarter));
        var from = new DateOnly(year, (quarter - 1) * 3 + 1, 1);
        var to = from.AddMonths(3);
        return await ReturnAsync(issuerKey, from, to, ioss, cancellationToken);
    }

    /// <summary>The IOSS return is monthly (Art. 369s); <see cref="OssReturnAsync"/> with <c>ioss</c> serves a quarter, this a month.</summary>
    public Task<IReadOnlyList<OssReturnRow>> IossMonthAsync(string issuerKey, int year, int month, CancellationToken cancellationToken)
        => ReturnAsync(issuerKey, new DateOnly(year, month, 1), new DateOnly(year, month, 1).AddMonths(1), ioss: true, cancellationToken);

    private async Task<IReadOnlyList<OssReturnRow>> ReturnAsync(string issuerKey, DateOnly from, DateOnly to, bool ioss, CancellationToken cancellationToken)
    {
        var documents = await DocumentsAsync(issuerKey, from, to, cancellationToken);
        var rows = new List<(string Country, string Type, int Rate, long Taxable, long Vat, string Currency, string? Correction)>();
        foreach (var doc in documents.Where(d => !d.Business && d.Kind is InvoiceKind.Receipt or InvoiceKind.Invoice or InvoiceKind.CreditNote))
        {
            if (ioss != (doc.IossNumber is not null))
                continue;
            var sign = doc.IsCredit ? -1 : 1;
            string? correction = null;
            if (doc.IsCredit && doc.CreditedInvoiceDate is { } credited && (credited < from))
                correction = ioss ? $"{credited.Year:D4}-{credited.Month:D2}" : $"{credited.Year:D4}-Q{(credited.Month - 1) / 3 + 1}";
            foreach (var line in doc.Lines.Where(l => l.VatCode is "S" or "Z" && l.TaxCountry is { } c
                && (ioss || !string.Equals(c, doc.Issuer.Country, StringComparison.OrdinalIgnoreCase))))
            {
                rows.Add((line.TaxCountry!.ToUpperInvariant(), line.IsGoods ? "goods" : "services", line.VatRateBasisPoints,
                    sign * line.Net.ToMinorUnits(), sign * line.Vat.ToMinorUnits(), doc.Currency, correction));
            }
        }
        return rows
            .GroupBy(r => (r.Country, r.Type, r.Rate, r.Currency, r.Correction))
            .OrderBy(g => g.Key.Correction ?? "", StringComparer.Ordinal).ThenBy(g => g.Key.Country, StringComparer.Ordinal).ThenBy(g => g.Key.Type, StringComparer.Ordinal).ThenBy(g => g.Key.Rate)
            .Select(g => new OssReturnRow(g.Key.Country, g.Key.Type, g.Key.Rate, g.Sum(r => r.Taxable), g.Sum(r => r.Vat), g.Key.Currency, g.Key.Correction))
            .ToList();
    }

    public static InvoicingExportFile OssCsv(IReadOnlyList<OssReturnRow> rows, string fileName)
    {
        var csv = new Csv("MemberStateOfConsumption", "SupplyType", "VatRatePercent", "TaxableAmount", "VatAmount", "Currency", "CorrectionOfPeriod");
        foreach (var r in rows)
            csv.Row(r.MemberState, r.SupplyType, r.RateBasisPoints / 100m, Minor(r.TaxableMinorUnits, r.Currency), Minor(r.VatMinorUnits, r.Currency), r.Currency, r.CorrectionOfPeriod);
        return new InvoicingExportFile(fileName, "text/csv; charset=utf-8", csv.ToBytes());
    }

    // ---- DAC7 (GA6) -------------------------------------------------------------------------------------------------

    /// <summary>
    /// The DAC7 figures of every seller for <paramref name="year"/> (only meaningful in <c>ConnectedSellers</c> mode):
    /// consideration (gross paid, net of credit notes), number of relevant activities (lines sold) and the platform's
    /// fees per quarter (Annex V Section III B(2)(e)–(f)). Sale of goods and the categories the host marks
    /// <see cref="VatCategoryOptions.PersonalService"/> count; ticket sales are flagged "probably out of scope" until
    /// <see cref="Dac7ReportOptions.TicketSalesInScope"/> (D37). An excluded seller (fewer than 30 activities and at most
    /// EUR 2 000 consideration, Annex V Section I B(4)(d)) is marked, not dropped (§4.8).
    /// </summary>
    public async Task<IReadOnlyList<Dac7SellerRow>> Dac7Async(int year, CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        var from = new DateOnly(year, 1, 1);
        var to = from.AddYears(1);
        var rows = new List<Dac7SellerRow>();
        using var session = store.OpenAsyncSession();
        var documents = await session.Query<Invoice>()
            .Where(i => i.SellerOrganizationId != null && i.IssueDate >= from && i.IssueDate < to)
            .Take(100_000)
            .ToListAsync(cancellationToken);
        var fees = await session.Query<PlatformFeeEntry>()
            .Where(e => e.SellerOrganizationId != null && e.OccurredAtUtc >= from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) && e.OccurredAtUtc < to.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))
            .Take(100_000)
            .ToListAsync(cancellationToken);

        foreach (var seller in documents.Where(d => d.Kind is InvoiceKind.Receipt or InvoiceKind.Invoice or InvoiceKind.CreditNote && d.IssuerKey == d.SellerOrganizationId)
                     .GroupBy(d => d.SellerOrganizationId!))
        {
            var consideration = new long[4];
            var activities = new int[4];
            var ticketFlag = false;
            var currency = seller.First().Currency;
            foreach (var doc in seller)
            {
                var q = (doc.IssueDate.Month - 1) / 3;
                var sign = doc.IsCredit ? -1 : 1;
                foreach (var line in doc.Lines)
                {
                    var isTicket = line.Channel is { } ch && settings.Dac7.TicketChannels.Contains(ch, StringComparer.OrdinalIgnoreCase);
                    var personal = line.VatCategory is { } cat && settings.VatCategories.TryGetValue(cat, out var c) && c.PersonalService;
                    if (isTicket)
                    {
                        ticketFlag = true;
                        if (!settings.Dac7.TicketSalesInScope)
                            continue;
                    }
                    else if (!line.IsGoods && !personal)
                        continue;
                    consideration[q] += sign * line.Gross.ToMinorUnits();
                    if (!doc.IsCredit && line.Source == InvoiceLineSource.Item)
                        activities[q]++;
                }
            }
            var feeQuarters = new long[4];
            foreach (var fee in fees.Where(f => f.SellerOrganizationId == seller.Key))
                feeQuarters[(fee.OccurredAtUtc.Month - 1) / 3] += fee.SignedMinorUnits;

            var profile = await session.LoadAsync<SellerProfile>(SellerProfile.IdFor(seller.Key), cancellationToken);
            var dac7 = profile?.Dac7;
            var total = consideration.Sum();
            var excluded = activities.Sum() < 30 && total <= 200_000; // EUR 2 000 in cents
            rows.Add(new Dac7SellerRow(seller.Key, profile?.Trader.Name ?? seller.Key, dac7?.IsEntity ?? false, dac7?.Tin, dac7?.TinCountry, dac7?.VatId,
                dac7?.BirthDate, dac7?.PrimaryAddress, dac7?.BusinessRegistrationNumber, dac7?.Iban, dac7?.AccountHolder,
                consideration, activities, feeQuarters, currency, excluded, ticketFlag, dac7?.Missing() ?? ["profile"]));
        }
        return rows.OrderBy(r => r.Name, StringComparer.Ordinal).ToList();
    }

    public static InvoicingExportFile Dac7Csv(IReadOnlyList<Dac7SellerRow> rows, int year)
    {
        var csv = new Csv("SellerId", "Name", "Entity", "Tin", "TinCountry", "VatId", "BirthDate", "Address", "BusinessRegistrationNumber", "Iban", "AccountHolder",
            "ConsiderationQ1", "ConsiderationQ2", "ConsiderationQ3", "ConsiderationQ4", "ActivitiesQ1", "ActivitiesQ2", "ActivitiesQ3", "ActivitiesQ4",
            "FeesQ1", "FeesQ2", "FeesQ3", "FeesQ4", "Currency", "ExcludedSeller", "TicketSalesFlagged", "MissingData");
        foreach (var r in rows)
        {
            csv.Row([r.OrganizationId, r.Name, r.IsEntity, r.Tin, r.TinCountry, r.VatId, r.BirthDate, r.Address, r.BusinessRegistrationNumber, r.Iban, r.AccountHolder,
                .. r.ConsiderationPerQuarter.Select(v => (object)Minor(v, r.Currency)), .. r.ActivitiesPerQuarter.Select(v => (object)v),
                .. r.FeesPerQuarter.Select(v => (object)Minor(v, r.Currency)), r.Currency, r.Excluded, r.TicketSalesFlagged, string.Join(";", r.MissingData)]);
        }
        return new InvoicingExportFile($"dac7-{year}.csv", "text/csv; charset=utf-8", csv.ToBytes());
    }

    /// <summary>
    /// The DAC7 XML in the shape of the OECD DPI XML Schema v1.0 (<c>urn:oecd:ties:dpi:v1</c>), which the EU adopted for
    /// DAC7 exchanges and which member states (Belgium's MyMinfin upload among them) accept with national
    /// variations. ⚠️ Not validated against a member state's XSD here: the reporting member state's schema is the
    /// authority (validate before filing; D37). Excluded sellers are left out of the XML (they are not reportable) and
    /// stay in the CSV, marked.
    /// </summary>
    public InvoicingExportFile Dac7Xml(IReadOnlyList<Dac7SellerRow> rows, int year)
    {
        XNamespace dpi = "urn:oecd:ties:dpi:v1";
        XNamespace stf = "urn:oecd:ties:dpistf:v1";
        var settings = options.CurrentValue;
        var country = (settings.Dac7.ReportingCountry ?? commerce.CurrentValue.Country).ToUpperInvariant();
        var platform = settings.Platform;
        var now = time.GetUtcNow();
        var messageRef = $"{country}{year}{now.UtcDateTime:yyyyMMddHHmmss}";
        var docIndex = 0;
        XElement DocSpec() => new(dpi + "DocSpec",
            new XElement(stf + "DocTypeIndic", "OECD1"),
            new XElement(stf + "DocRefId", $"{messageRef}-{++docIndex}"));
        XElement Amount(string name, long minor, string currency) => new(dpi + name, new XAttribute("currCode", currency), (minor / (decimal)Math.Pow(10, Money.MinorUnitDigits(currency))).ToString("0.00", CultureInfo.InvariantCulture));

        var body = new XElement(dpi + "DPIBody",
            new XElement(dpi + "PlatformOperator",
                new XElement(dpi + "ResCountryCode", country),
                new XElement(dpi + "TIN", new XAttribute("issuedBy", country), settings.Dac7.PlatformTin ?? ""),
                new XElement(dpi + "Name", platform.Name ?? ""),
                new XElement(dpi + "Address", new XElement(dpi + "CountryCode", platform.Country), new XElement(dpi + "AddressFree", string.Join(", ", platform.AddressLines.Append($"{platform.PostalCode} {platform.City}".Trim())))),
                DocSpec()));
        foreach (var r in rows.Where(r => !r.Excluded))
        {
            var tinCountry = (r.TinCountry ?? country).ToUpperInvariant();
            var standard = new XElement(dpi + "Standard",
                new XElement(dpi + (r.IsEntity ? "EntSellerID" : "IndSellerID"),
                    new XElement(dpi + "ResCountryCode", tinCountry),
                    new XElement(dpi + "TIN", new XAttribute("issuedBy", tinCountry), r.Tin ?? ""),
                    r.VatId is { Length: > 0 } ? new XElement(dpi + "VAT", r.VatId) : null,
                    r.IsEntity
                        ? new XElement(dpi + "Name", r.Name)
                        : new XElement(dpi + "Name", new XElement(dpi + "FirstName", ""), new XElement(dpi + "LastName", r.Name)),
                    new XElement(dpi + "Address", new XElement(dpi + "CountryCode", tinCountry), new XElement(dpi + "AddressFree", r.Address ?? "")),
                    !r.IsEntity && r.BirthDate is { } birth ? new XElement(dpi + "BirthInfo", new XElement(dpi + "BirthDate", birth.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))) : null),
                r.Iban is { Length: > 0 } ? new XElement(dpi + "FinancialIdentifier", new XElement(dpi + "Identifier", new XAttribute("AccountNumberType", "IBAN"), r.Iban), new XElement(dpi + "AccountHolderName", r.AccountHolder ?? r.Name)) : null);
            var seller = new XElement(dpi + "ReportableSeller",
                new XElement(dpi + "Identity", new XElement(dpi + (r.IsEntity ? "EntitySeller" : "IndividualSeller"), standard)),
                new XElement(dpi + "RelevantActivities",
                    new XElement(dpi + "SaleOfGoods",
                        new XElement(dpi + "Consideration", r.ConsiderationPerQuarter.Select((v, i) => Amount($"ConsQ{i + 1}", v, r.Currency))),
                        new XElement(dpi + "NumberOfActivities", r.ActivitiesPerQuarter.Select((v, i) => new XElement(dpi + $"NumbQ{i + 1}", v))),
                        new XElement(dpi + "Fees", r.FeesPerQuarter.Select((v, i) => Amount($"FeesQ{i + 1}", v, r.Currency))),
                        new XElement(dpi + "Taxes", Enumerable.Range(1, 4).Select(i => Amount($"TaxQ{i}", 0, r.Currency))))),
                DocSpec());
            body.Add(seller);
        }
        var root = new XElement(dpi + "DPI_OECD", new XAttribute("version", "1.0"),
            new XAttribute(XNamespace.Xmlns + "dpi", dpi.NamespaceName), new XAttribute(XNamespace.Xmlns + "stf", stf.NamespaceName),
            new XElement(dpi + "MessageSpec",
                new XElement(dpi + "SendingEntityIN", settings.Dac7.PlatformTin ?? ""),
                new XElement(dpi + "TransmittingCountry", country),
                new XElement(dpi + "ReceivingCountry", country),
                new XElement(dpi + "MessageType", "DPI"),
                new XElement(dpi + "MessageRefId", messageRef),
                new XElement(dpi + "MessageTypeIndic", "DPI401"),
                new XElement(dpi + "ReportingPeriod", $"{year:D4}-12-31"),
                new XElement(dpi + "Timestamp", now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture))),
            body);
        using var stream = new MemoryStream();
        new XDocument(new XDeclaration("1.0", "UTF-8", null), root).Save(stream);
        return new InvoicingExportFile($"dac7-{year}.xml", "application/xml", stream.ToArray());
    }

    // ---- accounting export (GB4) -------------------------------------------------------------------------------------

    /// <summary>
    /// An issuer's accounting export for a period, as one zip: <c>sales-journal.csv</c> (one row per document and VAT
    /// group, credit notes negative), <c>payments-journal.csv</c> (the settlement lines of the reconciliation, §4.5) and
    /// <c>ubl/{number}.xml</c> for every document of the period (UBL batch). Column sets are documented in the guide.
    /// </summary>
    public async Task<InvoicingExportFile> AccountingAsync(string issuerKey, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var documents = await DocumentsAsync(issuerKey, from, to, cancellationToken);
        var sales = new Csv("Date", "Number", "Kind", "Order", "CreditedNumber", "Buyer", "BuyerVatId", "BuyerCountry", "VatCode", "VatRatePercent", "TaxCountry",
            "TaxableAmount", "VatAmount", "GrossAmount", "Currency", "PlaceOfSupplyRule");
        foreach (var doc in documents)
        {
            var sign = doc.IsCredit ? -1 : 1;
            foreach (var group in doc.Lines.GroupBy(l => (l.VatCode, l.VatRateBasisPoints, l.TaxCountry, l.PlaceOfSupplyRule)))
            {
                sales.Row(doc.IssueDate, doc.Number, doc.Kind.ToString(), doc.OrderNumber, doc.CreditedInvoiceNumber, doc.Buyer.Name, doc.Buyer.VatId, doc.Buyer.Country,
                    group.Key.VatCode, group.Key.VatRateBasisPoints / 100m, group.Key.TaxCountry,
                    sign * group.Sum(l => l.Net.Amount), sign * group.Sum(l => l.Vat.Amount), sign * group.Sum(l => l.Gross.Amount), doc.Currency, group.Key.PlaceOfSupplyRule);
            }
        }

        var payments = new Csv("Date", "Provider", "SettlementId", "SettlementReference", "LineId", "Kind", "SourceId", "Gross", "Fee", "Net", "Currency", "Match", "MatchedDocumentId");
        var seller = issuerKey == InvoiceIssuers.PlatformKey ? null : issuerKey;
        using (var session = store.OpenAsyncSession())
        {
            var fromUtc = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            var toUtc = to.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            var lines = await session.Query<SettlementLine>()
                .Where(l => l.SellerOrganizationId == seller && l.OccurredAtUtc >= fromUtc && l.OccurredAtUtc < toUtc)
                .Take(100_000)
                .ToListAsync(cancellationToken);
            foreach (var l in lines.OrderBy(l => l.OccurredAtUtc))
                payments.Row(l.OccurredAtUtc, l.Provider, l.SettlementId, l.SettlementReference, l.LineId, l.Kind.ToString(), l.SourceId,
                    l.Gross.Amount, l.Fee?.Amount, l.Net.Amount, l.Gross.Currency, l.Match.ToString(), l.MatchedDocumentId);
        }

        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(archive, "sales-journal.csv", sales.ToBytes());
            Add(archive, "payments-journal.csv", payments.ToBytes());
            foreach (var doc in documents)
                Add(archive, "ubl/" + doc.Number + ".xml", UblWriter.Write(doc));
        }
        return new InvoicingExportFile($"accounting-{from:yyyyMMdd}-{to:yyyyMMdd}.zip", "application/zip", zip.ToArray());
    }

    private static void Add(ZipArchive archive, string name, byte[] content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(content);
    }

    private async Task<List<Invoice>> DocumentsAsync(string issuerKey, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var list = await session.Query<Invoice>()
            .Where(i => i.IssuerKey == issuerKey && i.IssueDate >= from && i.IssueDate < to)
            .Take(100_000)
            .ToListAsync(cancellationToken);
        return list.OrderBy(i => i.Series, StringComparer.Ordinal).ThenBy(i => i.Sequence).ToList();
    }

    private static decimal Minor(long minor, string currency) => minor / (decimal)Math.Pow(10, Money.MinorUnitDigits(currency));
}
