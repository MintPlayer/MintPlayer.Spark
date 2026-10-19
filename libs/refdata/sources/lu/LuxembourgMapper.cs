using System.Globalization;
using System.Text;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.ReferenceData.Build;
using static MintPlayer.Spark.ReferenceData.Sources.RefAdapterKit;

namespace MintPlayer.Spark.ReferenceData.Sources.Luxembourg;

/// <summary>
/// The Luxembourg mapper (PRD §4.4, plan R8): CACLR's fixed-width files (ISO-8859-1, CRLF; layouts from ACT's
/// "Spécifications des fichiers adresses" v2.2, with the extra current/former flag of <c>COMMUALL</c>) into cantons,
/// communes, localities, streets and postal codes.
/// <list type="bullet">
/// <item>A commune's code is unique only within its canton, so its identity is <c>canton + code</c> (<c>0814</c>).</item>
/// <item><c>COMMUALL</c> also lists former communes (flag <c>N</c>), but its date column is a modification stamp,
/// not a legal date (Grosbous, merged into Groussbus-Wal on 2023-09-01, carries 2009): they are skipped and history is
/// known only from the first import (RD4, PRD §4.4).</item>
/// <item>Street ↔ postcode links come only through the buildings file (<c>IMMEUBLE</c>), streamed; no house number is
/// kept (RD5). The postal city line is the postcode's own label (<c>ESCH/ALZETTE</c>, <c>GREVELS</c> for a street of Wahl).</item>
/// <item>Names are the register's official forms (French or German, as the communes chose; no separate Luxembourgish
/// names), tagged <c>fr</c>, the register's administrative language.</item>
/// </list>
/// </summary>
internal sealed class LuxembourgMapper : IReferenceDataMapper
{
    private const string Source = LuxembourgCaclrSource.SourceId;

    public IReadOnlyList<string> Countries => ["lu"];

    public IReadOnlyList<string> SourceIds => [Source];

    public int Order => 10;

    public bool IsCountryAdapter => true;

    public IEnumerable<LedgerEntry> ShippedLedger() => EmbeddedSourceFiles.Ledger(typeof(LuxembourgMapper).Assembly);

    public Task MapAsync(RefDataBuild build, IReadOnlyDictionary<string, SourceInput> inputs, CancellationToken cancellationToken)
    {
        if (!inputs.TryGetValue(Source, out var input))
            return Task.CompletedTask;

        Country(build, "lu", "LUX", "442", LuxembourgProfile.Create(), Source);
        var country = CountryPlace(build, "lu", Source, LuxembourgProfile.Zone, "fr", "de", "lb");

        // Cantons.
        foreach (var line in Lines(input, "CANTON"))
        {
            var code = Field(line, 1, 2);
            var id = Canton(build, code);
            var place = build.Place(id);
            place.Periods = [new PlacePeriod { Kind = "lu:canton", Role = PlaceRole.Region, Parent = country }];
            place.Names = new TranslatedString { Entries = [Name("fr", TitleCase(Field(line, 3, 40)), TranslatedStringKind.Official, Source)] };
            Common(place, "caclr-canton", code);
        }

        // Communes: current ones only (see the class remarks).
        foreach (var line in Lines(input, "COMMUALL"))
        {
            var canton = Field(line, 93, 2);
            var code = canton + Field(line, 1, 2);
            if (Field(line, 95, 1) != "O")
            {
                build.Warnings.Add($"{Source}: former commune {code} {Field(line, 3, 40)} skipped: CACLR states no legal end date.");
                continue;
            }
            var id = build.Ledger.Mint(Source, "lu-commune:" + code, RefIds.Place("lu", "commune", code));
            var place = build.Place(id);
            place.Periods = [new PlacePeriod { Kind = "lu:commune", Role = PlaceRole.Municipality, Parent = Canton(build, canton) }];
            place.Names = new TranslatedString { Entries = [Name("fr", Field(line, 3, 40), TranslatedStringKind.Official, Source)] };
            Common(place, "caclr-commune", code);
        }

        // Localities.
        var localities = new Dictionary<string, (string Id, string Municipality, string Name)>(StringComparer.Ordinal);
        foreach (var line in Lines(input, "LOCALITE"))
        {
            var number = Field(line, 1, 5);
            var commune = Field(line, 110, 2) + Field(line, 113, 2);
            var municipality = build.Ledger.Find("lu-commune:" + commune);
            if (municipality is null || !build.Places.ContainsKey(municipality))
            {
                build.Warnings.Add($"{Source}: locality {number} names commune {commune}, which is not current: skipped.");
                continue;
            }
            var name = Field(line, 6, 40);
            var id = build.Ledger.Mint(Source, "lu-localite:" + number, RefIds.Place("lu", "localite", number));
            var place = build.Place(id);
            var ended = Field(line, 99, 1) == "?" ? null : Day(Field(line, 89, 10));
            place.Periods = [new PlacePeriod { Kind = "lu:locality", Role = PlaceRole.Locality, Parent = municipality, To = ended }];
            place.Names = new TranslatedString { Entries = [Name("fr", name, TranslatedStringKind.Official, Source)] };
            Common(place, "caclr-localite", number);
            localities[number] = (id, municipality, name);
        }

        // Postal codes: the label of each code (the city line); PO-box ranges (type B) carry no streets.
        var postalLabels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in Lines(input, "CODEPT"))
            if (Field(line, 45, 1) != "B")
                postalLabels[Field(line, 1, 4)] = Field(line, 5, 40);

        // Streets.
        var streets = new Dictionary<string, (RefStreet Street, string Municipality, string Locality, string LocalityName)>(StringComparer.Ordinal);
        foreach (var line in Lines(input, "RUE"))
        {
            var number = Field(line, 1, 5);
            var localityNumber = Field(line, 137, 1) == "?" ? string.Empty : Field(line, 132, 5);
            if (!localities.TryGetValue(localityNumber, out var locality))
                continue;
            var id = build.Ledger.Mint(Source, "lu-rue:" + number, RefIds.Street("lu", "caclr", number));
            var street = build.Street(id, "lu");
            street.Names = new TranslatedString { Entries = [Name("fr", Field(line, 6, 40), TranslatedStringKind.Official, Source)] };
            street.Type = Field(line, 102, 1) == "O" ? StreetType.Hamlet : StreetType.Streetname;
            street.Codes = [new PlaceCode { Scheme = "caclr-rue", Value = number }];
            street.Source = [Source];
            var ended = Field(line, 113, 1) == "?" ? null : Day(Field(line, 103, 10));
            StreetIn(street, locality.Municipality, null, locality.Id, to: ended);
            streets[number] = (street, locality.Municipality, locality.Id, locality.Name);
        }

        // Street ↔ postcode links through the buildings (current buildings only; no house numbers kept).
        foreach (var line in Lines(input, "IMMEUBLE"))
        {
            if (Field(line, 28, 1) != "?" && Field(line, 18, 10).Length > 0)
                continue;
            if (Field(line, 55, 1) == "?" || Field(line, 43, 1) == "?")
                continue;
            var code = Field(line, 39, 4);
            if (!streets.TryGetValue(Field(line, 50, 5), out var street) || !postalLabels.TryGetValue(code, out var label))
                continue;
            var postalId = build.Ledger.Mint(Source, "postcode:" + code, RefIds.PostalCode("lu", code));
            var postal = build.PostalCode(postalId, "lu", code);
            var name = TitleCase(label);
            var place = Key(name) == Key(street.LocalityName) ? street.Locality : null;
            Locality(postal, name, "fr", Source, street.Municipality, place, main: place is not null);
            StreetIn(street.Street, street.Municipality, postalId, street.Locality);
        }

        return Task.CompletedTask;
    }

    private static string Canton(RefDataBuild build, string code)
        => build.Ledger.Mint(Source, "lu-canton:" + code, RefIds.Place("lu", "canton", code));

    private static void Common(RefPlace place, string scheme, string code)
    {
        place.OfficialLanguages = [new() { Lang = "fr" }, new() { Lang = "de" }, new() { Lang = "lb" }];
        place.Codes = [new PlaceCode { Scheme = scheme, Value = code }];
        place.TimeZone = LuxembourgProfile.Zone;
        place.Source = [Source];
    }

    /// <summary>The lines of the file named <paramref name="name"/> (ISO-8859-1, CRLF).</summary>
    private static IEnumerable<string> Lines(SourceInput input, string name)
    {
        var file = input.Files.FirstOrDefault(f => f.Name == name || f.Name == name + ".cut");
        if (file is null) yield break;
        using var reader = new StreamReader(file.Open(), Encoding.Latin1);
        while (reader.ReadLine() is { } line)
            if (line.Length > 0)
                yield return line;
    }

    /// <summary>The trimmed fixed-width field at 1-based <paramref name="position"/> (the specification's numbering).</summary>
    internal static string Field(string line, int position, int length)
        => position - 1 >= line.Length ? string.Empty : line.Substring(position - 1, Math.Min(length, line.Length - position + 1)).Trim();

    private static DateOnly? Day(string text)
        => DateOnly.TryParseExact(text, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;

    private static readonly HashSet<string> Particles = ["sur", "sous", "la", "le", "les", "de", "du", "des", "et", "am", "an", "der", "op", "zu"];

    /// <summary>Upper-case register labels (<c>ESCH/ALZETTE</c>, <c>REDANGE</c>) in title case: <c>Esch/Alzette</c>.</summary>
    internal static string TitleCase(string upper)
    {
        var result = new StringBuilder();
        var word = new StringBuilder();
        void Flush()
        {
            var w = word.ToString().ToLowerInvariant();
            result.Append(result.Length > 0 && Particles.Contains(w) ? w : w.Length > 0 ? char.ToUpperInvariant(w[0]) + w[1..] : w);
            word.Clear();
        }
        foreach (var c in upper)
        {
            if (c is ' ' or '-' or '/' or '\'')
            {
                Flush();
                result.Append(c);
            }
            else
                word.Append(c);
        }
        Flush();
        return result.ToString();
    }
}
