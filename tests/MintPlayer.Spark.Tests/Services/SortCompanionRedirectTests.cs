using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// The mechanism the whole <c>[GenerateIndex]</c> feature rests on, pinned against a real server.
/// <para>
/// A field indexed <c>FieldIndexing.Search</c> is analyzed and tokenized, so <c>"Volkswagen Golf GTI"</c> is
/// stored as three separate terms and ordering documents by "their" term is arbitrary. A companion field with
/// <strong>no <c>Index(...)</c> call at all</strong> keeps RavenDB's default indexing — a single lower-cased,
/// un-tokenized term — and therefore sorts correctly and supports equality and prefix matching.
/// </para>
/// <para>
/// A space is the tokenization boundary, which is why the defect is invisible until it bites: a single-word
/// value yields one term either way, so an analyzed field <em>accidentally</em> sorts fine. Every fixture
/// value here therefore contains a space.
/// </para>
/// <para>
/// These assertions are deliberately about RavenDB behaviour rather than about Spark code. If a future RavenDB
/// upgrade changes the default analyzer, the generated companions stop working and this fails — which is the
/// point. Nothing else in the suite would notice.
/// </para>
/// </summary>
public class SortCompanionRedirectTests : SparkTestDriver
{
    public class Car
    {
        public string? Id { get; set; }
        public string Model { get; set; } = string.Empty;

        /// <summary>Same values as <see cref="Model"/>, but never declared analyzed anywhere.</summary>
        public string Trim { get; set; } = string.Empty;
    }

    /// <summary>
    /// Mirrors exactly what the generator emits: the base field declared <c>Search</c>, the companion mapped
    /// from the same expression and left undeclared, and the blanket <c>StoreAllFields</c>.
    /// </summary>
    public class Cars_Overview : AbstractIndexCreationTask<Car>
    {
        public Cars_Overview()
        {
            Map = cars => from car in cars
                          select new
                          {
                              car.Model,
                              ModelSort = car.Model,
                              car.Trim,
                          };
            Index(nameof(VCar.Model), FieldIndexing.Search);
            StoreAllFields(FieldStorage.Yes);
        }
    }

    [FromIndex(typeof(Cars_Overview))]
    public class VCar
    {
        public string? Id { get; set; }
        public string Model { get; set; } = string.Empty;

        [IgnoreProperty]
        public string ModelSort { get; set; } = string.Empty;

        public string Trim { get; set; } = string.Empty;
    }

    /// <summary>Mixed case and interleaved initials, so a case-sensitive ordering is distinguishable.</summary>
    private static readonly string[] Models =
    [
        "Volkswagen Golf GTI",
        "Audi A4",
        "alfa romeo spider",
        "Zeta One",
        "ZZ Top",
    ];

    private async Task SeedAsync()
    {
        using var session = Store.OpenAsyncSession();
        foreach (var model in Models)
            await session.StoreAsync(new Car { Model = model, Trim = model });
        await session.SaveChangesAsync();

        await new Cars_Overview().ExecuteAsync(Store);
        await RavenIndexHelper.WaitForNonStaleAsync(Store);
    }

    private async Task<List<string>> OrderByAnalyzedFieldAsync()
    {
        using var session = Store.OpenAsyncSession();
        var results = await session.Query<VCar, Cars_Overview>()
            .OrderBy(v => v.Model)
            .ProjectInto<VCar>()
            .ToListAsync();

        return results.Select(v => v.Model).ToList();
    }

    private async Task<List<string>> OrderByCompanionAsync()
    {
        using var session = Store.OpenAsyncSession();
        var results = await session.Query<VCar, Cars_Overview>()
            .OrderBy(v => v.ModelSort)
            .ProjectInto<VCar>()
            .ToListAsync();

        return results.Select(v => v.Model).ToList();
    }

    /// <summary>
    /// The defect being repaired. Not asserting a specific wrong order — that is arbitrary by nature — only
    /// that the analyzed field does not produce the correct one.
    /// </summary>
    [Fact]
    public async Task Ordering_by_an_analyzed_field_does_not_sort_correctly()
    {
        await SeedAsync();

        var actual = await OrderByAnalyzedFieldAsync();
        var expected = Models.OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();

        actual.Should().NotEqual(expected);
    }

    [Fact]
    public async Task Ordering_by_the_undeclared_companion_sorts_correctly_and_case_insensitively()
    {
        await SeedAsync();

        var actual = await OrderByCompanionAsync();

        // Case-insensitive: "alfa romeo spider" sorts between "Audi A4"'s neighbours by letter, not after
        // every capitalised value, and "Zeta One" precedes "ZZ Top".
        actual.Should().Equal(
            "alfa romeo spider",
            "Audi A4",
            "Volkswagen Golf GTI",
            "Zeta One",
            "ZZ Top");
    }

    /// <summary>
    /// The companion is reachable from LINQ even though <c>[IgnoreProperty]</c> hides it from the model — the
    /// base field cannot serve equality, because <c>==</c> against an analyzed field is a full-text match.
    /// </summary>
    [Fact]
    public async Task The_companion_supports_exact_and_prefix_matching()
    {
        await SeedAsync();

        using var session = Store.OpenAsyncSession();

        var exact = await session.Query<VCar, Cars_Overview>()
            .Where(v => v.ModelSort == "Audi A4")
            .ProjectInto<VCar>()
            .ToListAsync();
        exact.Should().ContainSingle().Which.Model.Should().Be("Audi A4");

        var prefix = await session.Query<VCar, Cars_Overview>()
            .Where(v => v.ModelSort.StartsWith("Volkswagen"))
            .ProjectInto<VCar>()
            .ToListAsync();
        prefix.Should().ContainSingle().Which.Model.Should().Be("Volkswagen Golf GTI");
    }

    /// <summary>
    /// The question this answers: does a plain <c>string</c> field — no <c>[Search]</c>, no <c>Index(...)</c> call,
    /// no companion — sort correctly on its own?
    /// <para>
    /// It does, and that is why companions are scoped to analyzed fields rather than given to every string. An
    /// undeclared field keeps RavenDB's default indexing, which lower-cases but does not tokenize, so the whole
    /// value stays a single term. Declaring <c>Search</c> is what breaks it; the companion is the repair for that,
    /// not a general requirement.
    /// </para>
    /// <para>Same fixture values as the analyzed field, all containing spaces, so this is a like-for-like
    /// comparison against <see cref="Ordering_by_an_analyzed_field_does_not_sort_correctly"/>.</para>
    /// </summary>
    [Fact]
    public async Task A_plain_string_field_with_no_companion_sorts_correctly()
    {
        await SeedAsync();

        using var session = Store.OpenAsyncSession();
        var results = await session.Query<VCar, Cars_Overview>()
            .OrderBy(v => v.Trim)
            .ProjectInto<VCar>()
            .ToListAsync();

        results.Select(v => v.Trim).Should().Equal(
            "alfa romeo spider",
            "Audi A4",
            "Volkswagen Golf GTI",
            "Zeta One",
            "ZZ Top");
    }

    /// <summary>
    /// And the same field ordered through the query pipeline's redirect: with no companion to redirect to, it falls
    /// back to the field itself, which is correct. Nothing is silently left unsorted.
    /// </summary>
    [Fact]
    public async Task A_plain_string_field_sorts_the_same_as_an_analyzed_field_plus_its_companion()
    {
        await SeedAsync();

        var viaCompanion = await OrderByCompanionAsync();

        using var session = Store.OpenAsyncSession();
        var viaPlainField = (await session.Query<VCar, Cars_Overview>()
            .OrderBy(v => v.Trim)
            .ProjectInto<VCar>()
            .ToListAsync()).Select(v => v.Model).ToList();

        viaPlainField.Should().Equal(viaCompanion);
    }

    /// <summary>
    /// Without <c>StoreAllFields</c> a projection-only field comes back null while the index stays provably
    /// correct — no error, no index fault. Pinned because it is the likeliest way a generated index looks
    /// broken, and because nothing else would catch its removal.
    /// </summary>
    [Fact]
    public async Task StoreAllFields_is_what_makes_the_companion_readable()
    {
        await SeedAsync();

        using var session = Store.OpenAsyncSession();
        var all = await session.Query<VCar, Cars_Overview>().ProjectInto<VCar>().ToListAsync();

        all.Should().OnlyContain(v => v.ModelSort.Length > 0);
    }
}
