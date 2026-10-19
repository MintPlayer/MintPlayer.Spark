using System.Text;
using System.Text.RegularExpressions;

namespace MintPlayer.Spark.ReferenceData.Sources.Estonia;

/// <summary>Credit lines (PRD §4.4, §4.12; research §4).</summary>
internal static class EstoniaLicences
{
    /// <summary>EHAK and ADS: "no license agreement" (ADS), credit the board; AKS data CC0 (research §2).</summary>
    public static readonly SourceLicence Ehak = new(
        "Maa- ja Ruumiamet EHAK", "Open data (no licence agreement)", "https://geoportaal.maaamet.ee/est/Ruumiandmed/Haldus-ja-asustusjaotus-p119.html",
        "Source: Estonian Land and Spatial Development Board (Maa- ja Ruumiamet), Estonian administrative and settlement classification (EHAK). The data was modified: units are linked into a dated hierarchy.");

    public static readonly SourceLicence Ads = new(
        "Maa- ja Ruumiamet ADS", "Open data (no licence agreement)", "https://geoportaal.maaamet.ee/est/Ruumiandmed/Aadressiandmed-p112.html",
        "Source: Estonian Land and Spatial Development Board (Maa- ja Ruumiamet), Address Data System (ADS). The data was modified: streets are derived from the address components with their history; postcodes and house numbers are not kept.");
}

/// <summary>
/// EHAK (<c>ee-ehak</c>): the settlement-unit layer (<c>asustusyksus_shp.zip</c>), whose attribute table names each
/// settlement unit with its municipality and county (codes and names): the whole current three-level hierarchy in
/// one DBF (Windows-1257). Change check: <c>ETag</c> (the bucket is S3). Monthly, with a 1 January snapshot.
/// </summary>
internal sealed partial class EstoniaEhakSource : RefFileSource
{
    public const string SourceId = "ee-ehak";

    public override string Id => SourceId;
    public override string Country => "ee";
    public override SourceLicence Licence => EstoniaLicences.Ehak;
    public override IReadOnlyList<string> DownloadHosts => ["s3.pilw.io"];
    public override string DefaultSchedule => "0 0 5 3 * *";
    protected override string DownloadUrl => "https://s3.pilw.io/rp-kemit-kataster/EHAK/asustusyksus_shp.zip";
    protected override Regex? EntryPattern => DbfFile();
    protected override long MaxBytes => 256L * 1024 * 1024;
    protected override IReadOnlyList<string> FixtureNames => ["asustusyksus.cut.dbf"];

    [GeneratedRegex(@"(?i)^asustusyksus.*\.dbf$")]
    internal static partial Regex DbfFile();
}

/// <summary>
/// The ADS address extract with components and history (<c>ee-ads</c>, extract nr 9 on
/// <see href="https://aks.maaruum.ee/aks/valjavotted"/>, CSV, every 30 days). The extract's file URL is served by the
/// AKS web application and is not published as a stable link, so the host states it in
/// <c>Spark:ReferenceData:Sources:ee-ads:Url</c>. ⚠️ <b>"The access is limited for foreign IP addresses"</b>: run the
/// import where the download is allowed. Postcodes are in the extract but are not imported: the postcode file states
/// no licence, so republishing them waits for that to be verified (PRD §4.4).
/// </summary>
internal sealed partial class EstoniaAdsSource : RefFileSource
{
    public const string SourceId = "ee-ads";

    /// <summary>The extract's URL, from configuration (set by <c>AddEstonia()</c>).</summary>
    internal Func<string?> Url { get; set; } = () => null;

    public override string Id => SourceId;
    public override string Country => "ee";
    public override SourceLicence Licence => EstoniaLicences.Ads;
    public override IReadOnlyList<string> DownloadHosts => ["aks.maaruum.ee", "aks.maaamet.ee"];
    public override string DefaultSchedule => "0 0 5 5 * *";
    protected override string DownloadUrl => Url() ?? throw new InvalidOperationException(
        "ee-ads: set Spark:ReferenceData:Sources:ee-ads:Url to the ADS extract nr 9 file (https://aks.maaruum.ee/aks/valjavotted).");
    protected override Regex? EntryPattern => CsvFile();
    protected override IReadOnlyList<string> FixtureNames => ["ads-valjavote-9.docderived.csv"];

    [GeneratedRegex(@"(?i)\.csv$")]
    internal static partial Regex CsvFile();
}

/// <summary>A minimal dBASE III reader for shapefile attribute tables (character, numeric and date fields).</summary>
internal static class DbfReader
{
    public static IEnumerable<IReadOnlyDictionary<string, string>> Read(Stream stream, Encoding encoding)
    {
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: false);
        reader.ReadBytes(4);
        var count = reader.ReadInt32();
        var headerLength = reader.ReadInt16();
        var recordLength = reader.ReadInt16();
        reader.ReadBytes(20);
        var fields = new List<(string Name, int Length)>();
        var read = 32;
        while (true)
        {
            var first = reader.ReadByte();
            read++;
            if (first == 0x0D) break;
            var descriptor = reader.ReadBytes(31);
            read += 31;
            var nameBytes = new[] { first }.Concat(descriptor.Take(10)).TakeWhile(b => b != 0).ToArray();
            fields.Add((Encoding.ASCII.GetString(nameBytes), descriptor[15]));
        }
        reader.ReadBytes(headerLength - read);
        for (var i = 0; i < count; i++)
        {
            var record = reader.ReadBytes(recordLength);
            if (record.Length < recordLength) yield break;
            if (record[0] == (byte)'*') continue; // deleted
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var offset = 1;
            foreach (var (name, length) in fields)
            {
                values[name] = encoding.GetString(record, offset, length).Trim();
                offset += length;
            }
            yield return values;
        }
    }

    /// <summary>Windows-1257 (Baltic), which EHAK's attribute tables use (LDID 3 says only "Windows ANSI").</summary>
    public static Encoding Baltic()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1257);
    }
}
