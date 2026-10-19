using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.ReferenceData.Build;

namespace MintPlayer.Spark.ReferenceData.Sources.Holidays;

/// <summary>
/// Statutory public holidays (<c>statute-holidays</c>, commerce GC11) of the seeded countries, computed from the statute
/// that lists them: fixed dates and the Easter cycle (Gregorian computus). No upstream file: the statutes are the
/// source, so the change check never reports a change and the "fixture" is the rule set itself.
/// </summary>
/// <remarks>
/// Only <b>national</b> statutory days. Regional days (German Länder, Alsace-Moselle) and days set by collective
/// agreement are not here: their open sources are chosen in R0 (PRD §4.4 "Optional sources"); until then a country's
/// regional calendar is "no holiday data". ⚠️ The citations were not checked against the official journals in this
/// session; verify them (commerce D37) before a host relies on them.
/// </remarks>
internal sealed class StatuteHolidaysSource : IReferenceDataSource
{
    public const string SourceId = "statute-holidays";
    /// <summary>The years the rules are expanded for.</summary>
    public static readonly int[] Years = [2025, 2026, 2027];

    public string Id => SourceId;
    public string Country => "*";
    public SourceLicence Licence { get; } = new("National statutes", "Public law (no copyright on legislation)", null,
        "Statutory public holidays computed from the cited national statutes.", Modified: false);
    public IReadOnlyList<string> DownloadHosts => [];
    public string DefaultSchedule => "0 0 3 1 1 *";

    public Task<SourceVersion?> CheckAsync(SourceState? last, HttpClient http, CancellationToken cancellationToken)
        => Task.FromResult<SourceVersion?>(last?.Version == RulesVersion ? null : new SourceVersion(RulesVersion, "statute:"));

    public Task<SourceInput> FetchAsync(SourceVersion version, HttpClient http, DirectoryInfo workDirectory, CancellationToken cancellationToken)
        => Task.FromResult(Fixture());

    public SourceInput Fixture() => new() { SourceId = Id, Version = RulesVersion, Files = [] };

    /// <summary>Bumped when a rule changes.</summary>
    public const string RulesVersion = "rules-1";
}

internal sealed class StatuteHolidaysMapper : IReferenceDataMapper
{
    public IReadOnlyList<string> Countries => ["be", "de", "fr", "lu", "nl"];
    public IReadOnlyList<string> SourceIds => [StatuteHolidaysSource.SourceId];
    public int Order => 30;
    public IEnumerable<LedgerEntry> ShippedLedger() => [];

    private const string Be = "Wet van 4 januari 1974 betreffende de feestdagen, art. 1; KB van 18 april 1974, art. 1";
    private const string Nl = "Algemene termijnenwet, art. 3";
    private const string Fr = "Code du travail, art. L3133-1";
    private const string Lu = "Code du travail, art. L.232-2";
    private const string De = "Einigungsvertrag, Art. 2(2) (Tag der Deutschen Einheit; the other days are Land law)";

    public Task MapAsync(RefDataBuild build, IReadOnlyDictionary<string, SourceInput> inputs, CancellationToken cancellationToken)
    {
        if (!inputs.ContainsKey(StatuteHolidaysSource.SourceId))
            return Task.CompletedTask;
        foreach (var year in StatuteHolidaysSource.Years)
        {
            var easter = Easter(year);
            var ascension = easter.AddDays(39);
            var whitMonday = easter.AddDays(50);

            Add(build, "be", year, 1, 1, Be, ("nl", "Nieuwjaar"), ("fr", "Nouvel An"), ("de", "Neujahr"), ("en", "New Year's Day"));
            Add(build, "be", easter.AddDays(1), Be, ("nl", "Paasmaandag"), ("fr", "Lundi de Pâques"), ("de", "Ostermontag"), ("en", "Easter Monday"));
            Add(build, "be", year, 5, 1, Be, ("nl", "Feest van de Arbeid"), ("fr", "Fête du Travail"), ("de", "Tag der Arbeit"), ("en", "Labour Day"));
            Add(build, "be", ascension, Be, ("nl", "Onze-Lieve-Heer-Hemelvaart"), ("fr", "Ascension"), ("de", "Christi Himmelfahrt"), ("en", "Ascension Day"));
            Add(build, "be", whitMonday, Be, ("nl", "Pinkstermaandag"), ("fr", "Lundi de Pentecôte"), ("de", "Pfingstmontag"), ("en", "Whit Monday"));
            Add(build, "be", year, 7, 21, Be, ("nl", "Nationale feestdag"), ("fr", "Fête nationale"), ("de", "Nationalfeiertag"), ("en", "National Day"));
            Add(build, "be", year, 8, 15, Be, ("nl", "Onze-Lieve-Vrouw-Hemelvaart"), ("fr", "Assomption"), ("de", "Mariä Himmelfahrt"), ("en", "Assumption"));
            Add(build, "be", year, 11, 1, Be, ("nl", "Allerheiligen"), ("fr", "Toussaint"), ("de", "Allerheiligen"), ("en", "All Saints' Day"));
            Add(build, "be", year, 11, 11, Be, ("nl", "Wapenstilstand"), ("fr", "Armistice"), ("de", "Waffenstillstand"), ("en", "Armistice Day"));
            Add(build, "be", year, 12, 25, Be, ("nl", "Kerstmis"), ("fr", "Noël"), ("de", "Weihnachten"), ("en", "Christmas Day"));

            Add(build, "nl", year, 1, 1, Nl, ("nl", "Nieuwjaarsdag"), ("en", "New Year's Day"));
            Add(build, "nl", easter.AddDays(1), Nl, ("nl", "Tweede Paasdag"), ("en", "Easter Monday"));
            var kingsDay = new DateOnly(year, 4, 27);
            Add(build, "nl", kingsDay.DayOfWeek == DayOfWeek.Sunday ? kingsDay.AddDays(-1) : kingsDay, Nl, ("nl", "Koningsdag"), ("en", "King's Day"));
            Add(build, "nl", year, 5, 5, Nl, ("nl", "Bevrijdingsdag"), ("en", "Liberation Day"));
            Add(build, "nl", ascension, Nl, ("nl", "Hemelvaartsdag"), ("en", "Ascension Day"));
            Add(build, "nl", whitMonday, Nl, ("nl", "Tweede Pinksterdag"), ("en", "Whit Monday"));
            Add(build, "nl", year, 12, 25, Nl, ("nl", "Eerste Kerstdag"), ("en", "Christmas Day"));
            Add(build, "nl", year, 12, 26, Nl, ("nl", "Tweede Kerstdag"), ("en", "Boxing Day"));

            Add(build, "fr", year, 1, 1, Fr, ("fr", "Jour de l'an"), ("en", "New Year's Day"));
            Add(build, "fr", easter.AddDays(1), Fr, ("fr", "Lundi de Pâques"), ("en", "Easter Monday"));
            Add(build, "fr", year, 5, 1, Fr, ("fr", "Fête du Travail"), ("en", "Labour Day"));
            Add(build, "fr", year, 5, 8, Fr, ("fr", "Victoire 1945"), ("en", "Victory in Europe Day"));
            Add(build, "fr", ascension, Fr, ("fr", "Ascension"), ("en", "Ascension Day"));
            Add(build, "fr", whitMonday, Fr, ("fr", "Lundi de Pentecôte"), ("en", "Whit Monday"));
            Add(build, "fr", year, 7, 14, Fr, ("fr", "Fête nationale"), ("en", "Bastille Day"));
            Add(build, "fr", year, 8, 15, Fr, ("fr", "Assomption"), ("en", "Assumption"));
            Add(build, "fr", year, 11, 1, Fr, ("fr", "Toussaint"), ("en", "All Saints' Day"));
            Add(build, "fr", year, 11, 11, Fr, ("fr", "Armistice 1918"), ("en", "Armistice Day"));
            Add(build, "fr", year, 12, 25, Fr, ("fr", "Noël"), ("en", "Christmas Day"));

            Add(build, "lu", year, 1, 1, Lu, ("fr", "Jour de l'an"), ("de", "Neujahr"), ("lb", "Neijoerschdag"), ("en", "New Year's Day"));
            Add(build, "lu", easter.AddDays(1), Lu, ("fr", "Lundi de Pâques"), ("de", "Ostermontag"), ("lb", "Ouschterméindeg"), ("en", "Easter Monday"));
            Add(build, "lu", year, 5, 1, Lu, ("fr", "Fête du Travail"), ("de", "Tag der Arbeit"), ("lb", "Dag vun der Aarbecht"), ("en", "Labour Day"));
            Add(build, "lu", year, 5, 9, Lu, ("fr", "Journée de l'Europe"), ("de", "Europatag"), ("lb", "Europadag"), ("en", "Europe Day"));
            Add(build, "lu", ascension, Lu, ("fr", "Ascension"), ("de", "Christi Himmelfahrt"), ("lb", "Christi Himmelfaart"), ("en", "Ascension Day"));
            Add(build, "lu", whitMonday, Lu, ("fr", "Lundi de Pentecôte"), ("de", "Pfingstmontag"), ("lb", "Péngschtméindeg"), ("en", "Whit Monday"));
            Add(build, "lu", year, 6, 23, Lu, ("fr", "Fête nationale"), ("de", "Nationalfeiertag"), ("lb", "Nationalfeierdag"), ("en", "National Day"));
            Add(build, "lu", year, 8, 15, Lu, ("fr", "Assomption"), ("de", "Mariä Himmelfahrt"), ("lb", "Léiffrawëschdag"), ("en", "Assumption"));
            Add(build, "lu", year, 11, 1, Lu, ("fr", "Toussaint"), ("de", "Allerheiligen"), ("lb", "Allerhellgen"), ("en", "All Saints' Day"));
            Add(build, "lu", year, 12, 25, Lu, ("fr", "Noël"), ("de", "Weihnachten"), ("lb", "Chrëschtdag"), ("en", "Christmas Day"));
            Add(build, "lu", year, 12, 26, Lu, ("fr", "Saint-Étienne"), ("de", "Stephanstag"), ("lb", "Stiefesdag"), ("en", "St Stephen's Day"));

            Add(build, "de", year, 10, 3, De, ("de", "Tag der Deutschen Einheit"), ("en", "German Unity Day"));
        }
        return Task.CompletedTask;
    }

    private static void Add(RefDataBuild build, string country, int year, int month, int day, string basis, params (string Tag, string Name)[] names)
        => Add(build, country, new DateOnly(year, month, day), basis, names);

    private static void Add(RefDataBuild build, string country, DateOnly date, string basis, params (string Tag, string Name)[] names)
    {
        var id = RefIds.Holiday(country, null, date);
        build.Holidays[id] = new PublicHoliday
        {
            Id = id,
            Country = country,
            Date = date,
            Names = TranslatedString.Of(names),
            LegalBasis = basis,
            Source = [StatuteHolidaysSource.SourceId],
        };
    }

    /// <summary>Easter Sunday (the anonymous Gregorian algorithm, Meeus/Jones/Butcher).</summary>
    internal static DateOnly Easter(int year)
    {
        var a = year % 19;
        var b = year / 100;
        var c = year % 100;
        var d = b / 4;
        var e = b % 4;
        var f = (b + 8) / 25;
        var g = (b - f + 1) / 3;
        var h = (19 * a + b - d - g + 15) % 30;
        var i = c / 4;
        var k = c % 4;
        var l = (32 + 2 * e + 2 * i - h - k) % 7;
        var m = (a + 11 * h + 22 * l) / 451;
        var month = (h + l - 7 * m + 114) / 31;
        var day = (h + l - 7 * m + 114) % 31 + 1;
        return new DateOnly(year, month, day);
    }
}

public static class ReferenceDataHolidaysExtensions
{
    /// <summary>Statutory national holidays of BE, NL, FR, LU and DE (commerce GC11).</summary>
    public static ReferenceDataBuilder AddStatutoryHolidays(this ReferenceDataBuilder builder)
        => builder.AddSource<StatuteHolidaysSource>().AddMapper<StatuteHolidaysMapper>();
}
