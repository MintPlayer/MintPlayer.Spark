using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Queries;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// What a search term does once it is pushed into RavenDB instead of filtered in memory.
/// <para>
/// The framework used to materialize the whole collection and run <c>string.Contains</c> over the mapped rows.
/// Replacing that with <c>search(...)</c> is only safe if the term shape preserves substring matching, so these
/// assertions are mostly about RavenDB behaviour rather than about Spark code — if a future RavenDB version
/// stops honouring a leading wildcard, search silently narrows and only this fails.
/// </para>
/// <para>
/// Every fixture value contains a space, because a space is the tokenization boundary: single-word values
/// behave the same analyzed or not, which is what makes this class of defect invisible until it bites.
/// </para>
/// </summary>
public class SearchPushdownTests : SparkTestDriver
{
    public class Car
    {
        public string? Id { get; set; }

        /// <summary>Declared <c>Search</c> — analyzed and tokenized.</summary>
        public string Model { get; set; } = string.Empty;

        /// <summary>Never declared anywhere: one lower-cased, un-tokenized term.</summary>
        public string Trim { get; set; } = string.Empty;

        public string Kind { get; set; } = string.Empty;
    }

    public class Cars_Search : AbstractIndexCreationTask<Car>
    {
        public Cars_Search()
        {
            Map = cars => from car in cars
                          select new
                          {
                              car.Model,
                              ModelSort = car.Model,
                              car.Trim,
                              car.Kind,
                          };
            Index(nameof(VCar.Model), FieldIndexing.Search);
            StoreAllFields(FieldStorage.Yes);
        }
    }

    [FromIndex(typeof(Cars_Search))]
    public class VCar
    {
        public string? Id { get; set; }
        public string Model { get; set; } = string.Empty;

        [IgnoreProperty]
        public string ModelSort { get; set; } = string.Empty;

        public string Trim { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
    }

    private static readonly string[] Models =
    [
        "Volkswagen Golf GTI",
        "Volkswagen Up",
        "Audi A4",
        "alfa romeo spider",
        "Skoda Octavia",
    ];

    private async Task SeedAsync()
    {
        using var session = Store.OpenAsyncSession();
        foreach (var model in Models)
            await session.StoreAsync(new Car { Model = model, Trim = model, Kind = "car" });
        await session.SaveChangesAsync();

        await new Cars_Search().ExecuteAsync(Store);
        await RavenIndexHelper.WaitForNonStaleAsync(Store);
    }

    /// <summary>Applies the framework's own term shape, so these tests exercise what production emits.</summary>
    private async Task<List<string>> SearchAsync(string userInput, Func<VCar, object>? _ = null)
    {
        var term = QueryExecutor.BuildSearchTerm(userInput);
        term.Should().NotBeNull("the fixture inputs are all non-empty");

        using var session = Store.OpenAsyncSession();
        var results = await session.Query<VCar, Cars_Search>()
            .Search(v => v.Model, term, options: SearchOptions.Guess, @operator: SearchOperator.And)
            .ProjectInto<VCar>()
            .ToListAsync();

        return results.Select(v => v.Model).ToList();
    }

    // ---------------------------------------------------------------- term construction

    [Theory]
    [InlineData("volks", "*volks*")]
    [InlineData("volks gti", "*volks* *gti*")]
    [InlineData("  spaced   out  ", "*spaced* *out*")]
    public void A_term_wraps_every_word_in_wildcards(string input, string expected)
        => QueryExecutor.BuildSearchTerm(input).Should().Be(expected);

    /// <summary>
    /// Null, empty and whitespace must all collapse to "no search", because RavenDB does not treat them that
    /// way: an empty term returns <em>zero rows</em> and a null term throws. Without this the act of clearing
    /// the search box would empty every grid.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void An_empty_term_is_no_search_at_all(string? input)
        => QueryExecutor.BuildSearchTerm(input).Should().BeNull();

    /// <summary>
    /// A bare <c>*</c> matches every document, so a caller typing one must not be able to widen their own
    /// query. Wildcard characters are stripped from the input rather than honoured — <c>?</c> and a mid-word
    /// <c>*</c> were measured not to work anyway, so passing them through could only mislead.
    /// </summary>
    [Theory]
    [InlineData("*", null)]
    [InlineData("**", null)]
    [InlineData("?", null)]
    [InlineData("* ?", null)]
    [InlineData("vol*ks", "*volks*")]
    [InlineData("*volks*", "*volks*")]
    public void Caller_supplied_wildcards_are_stripped(string input, string? expected)
        => QueryExecutor.BuildSearchTerm(input).Should().Be(expected);

    // ---------------------------------------------------------------- matching semantics

    /// <summary>
    /// The whole reason for the wildcard wrapping: this is what the in-memory <c>Contains</c> used to do, and
    /// a bare term-based search would silently stop doing it.
    /// </summary>
    [Fact]
    public async Task An_infix_substring_matches_as_it_did_in_memory()
    {
        await SeedAsync();

        var results = await SearchAsync("olkswag");

        results.Should().BeEquivalentTo(["Volkswagen Golf GTI", "Volkswagen Up"]);
    }

    [Fact]
    public async Task A_prefix_matches()
    {
        await SeedAsync();

        (await SearchAsync("skod")).Should().BeEquivalentTo(["Skoda Octavia"]);
    }

    /// <summary>The point of an analyzed field: word order in the query must not matter.</summary>
    [Fact]
    public async Task A_multi_word_term_matches_regardless_of_word_order()
    {
        await SeedAsync();

        var forwards = await SearchAsync("volkswagen golf");
        var backwards = await SearchAsync("golf volkswagen");

        forwards.Should().BeEquivalentTo(["Volkswagen Golf GTI"]);
        backwards.Should().Equal(forwards);
    }

    /// <summary>
    /// <c>SearchOperator.And</c> is what makes a multi-word term narrow rather than widen. With the default
    /// <c>Or</c>, "volkswagen golf" would also return the Up.
    /// </summary>
    [Fact]
    public async Task Every_word_of_a_multi_word_term_must_match()
    {
        await SeedAsync();

        (await SearchAsync("volkswagen octavia")).Should().BeEmpty();
    }

    [Fact]
    public async Task Matching_is_case_insensitive_in_both_directions()
    {
        await SeedAsync();

        (await SearchAsync("VOLKSWAGEN UP")).Should().BeEquivalentTo(["Volkswagen Up"]);
        (await SearchAsync("alfa")).Should().BeEquivalentTo(["alfa romeo spider"]);
    }

    [Fact]
    public async Task A_term_matching_nothing_returns_nothing()
    {
        await SeedAsync();

        (await SearchAsync("ferrari")).Should().BeEmpty();
    }

    /// <summary>
    /// A substring spanning a space still matches — the interesting case, because it matches for a different
    /// reason than <c>Contains</c> did. Each word is wrapped separately, so <c>*olf*</c> and <c>*GT*</c> are
    /// matched independently rather than as one adjacent run.
    /// </summary>
    [Fact]
    public async Task A_substring_spanning_a_space_still_matches()
    {
        await SeedAsync();

        (await SearchAsync("olf GT")).Should().BeEquivalentTo(["Volkswagen Golf GTI"]);
    }

    /// <summary>
    /// The consequence of matching each word independently: the search is *more* permissive than the
    /// <c>Contains</c> it replaced, because the words need not be adjacent or in order.
    /// <c>Contains("gti golf")</c> would not have matched this row.
    /// <para>A widening rather than a narrowing, so no caller loses a result — pinned because it is a real
    /// behaviour difference and should be a documented choice, not an accident.</para>
    /// </summary>
    [Fact]
    public async Task Words_need_not_be_adjacent_or_in_order()
    {
        await SeedAsync();

        (await SearchAsync("gti golf")).Should().BeEquivalentTo(["Volkswagen Golf GTI"]);
        (await SearchAsync("wagen olf")).Should().BeEquivalentTo(["Volkswagen Golf GTI"]);
    }

    // ---------------------------------------------------------------- field selection

    /// <summary>
    /// Searchability is not scoped to <c>[Search]</c>. A plain default-indexed field holds the whole value as
    /// one term, so a bare word cannot match it — but a wildcard term can, which is why the framework searches
    /// every string field rather than only the declared ones.
    /// </summary>
    [Fact]
    public async Task A_field_that_was_never_declared_searchable_still_matches()
    {
        await SeedAsync();

        var term = QueryExecutor.BuildSearchTerm("olkswag");

        using var session = Store.OpenAsyncSession();
        var results = await session.Query<VCar, Cars_Search>()
            .Search(v => v.Trim, term, options: SearchOptions.Guess, @operator: SearchOperator.And)
            .ProjectInto<VCar>()
            .ToListAsync();

        results.Select(v => v.Trim).Should().BeEquivalentTo(["Volkswagen Golf GTI", "Volkswagen Up"]);
    }

    /// <summary>
    /// And the control that explains why the wrapping is not optional: the same field, the same value, a bare
    /// word instead of a wildcard term, matches nothing at all.
    /// </summary>
    [Fact]
    public async Task A_bare_word_does_not_match_an_undeclared_field()
    {
        await SeedAsync();

        using var session = Store.OpenAsyncSession();
        var results = await session.Query<VCar, Cars_Search>()
            .Search(v => v.Trim, "volkswagen")
            .ProjectInto<VCar>()
            .ToListAsync();

        results.Should().BeEmpty();
    }

    /// <summary>
    /// Sort companions are excluded from search. They hold the same text as the field they shadow, so
    /// searching both would double the clauses to find the same rows.
    /// </summary>
    [Fact]
    public void Sort_companions_are_not_searched()
    {
        var searched = SearchablePropertyNames(typeof(VCar));

        searched.Should().Contain("Model");
        searched.Should().NotContain("ModelSort");
    }

    /// <summary>
    /// <c>search(id(), …)</c> was measured to match nothing, so including the document id would only add a
    /// dead clause to every query.
    /// </summary>
    [Fact]
    public void The_document_id_is_not_searched()
        => SearchablePropertyNames(typeof(VCar)).Should().NotContain("Id");

    [Fact]
    public void Only_string_fields_are_searched()
    {
        var searched = SearchablePropertyNames(typeof(DatedCar));

        searched.Should().BeEquivalentTo(["Model"]);
    }

    private class DatedCar
    {
        public string? Id { get; set; }
        public string Model { get; set; } = string.Empty;
        public DateTimeOffset RegisteredOn { get; set; }
        public int Doors { get; set; }
    }

    private static string[] SearchablePropertyNames(Type type)
    {
        var method = typeof(QueryExecutor).GetMethod(
            "ResolveSearchableProperties",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        return ((System.Reflection.PropertyInfo[])method.Invoke(null, [type])!)
            .Select(p => p.Name)
            .ToArray();
    }

    // ---------------------------------------------------------------- composition with a row filter

    /// <summary>
    /// The failure mode that returns plausible rows instead of erroring, so nothing else would catch it.
    /// <para>
    /// RavenDB groups consecutive <c>search</c> clauses and ANDs the group with its neighbours — but only
    /// while <c>SearchOptions</c> is left at its default. Passing <c>SearchOptions.Or</c> explicitly makes the
    /// option leak onto the <em>adjacent</em> clause, and the adjacent clause here is the row-security
    /// predicate: a security filter would silently become an alternative.
    /// </para>
    /// <para>Asserted on the emitted RQL rather than on rows, because the wrong shape still returns
    /// results — just too many of them.</para>
    /// </summary>
    [Fact]
    public void A_search_group_is_ANDed_with_a_preceding_filter()
    {
        using var session = Store.OpenAsyncSession();
        var query = session.Query<VCar, Cars_Search>()
            .Where(v => v.Kind == "car")
            .Search(v => v.Model, "*volks*", options: SearchOptions.Guess, @operator: SearchOperator.And)
            .Search(v => v.Trim, "*volks*", options: SearchOptions.Guess, @operator: SearchOperator.And);

        var rql = query.ToString();

        rql.Should().Contain("and (search(Model");
        rql.Should().Contain("or search(Trim");
    }

    /// <summary>
    /// A row filter still excludes rows the search term matches. The counterpart to the RQL assertion above,
    /// this time on behaviour.
    /// </summary>
    [Fact]
    public async Task A_preceding_filter_still_excludes_matching_rows()
    {
        await SeedAsync();
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new Car { Model = "Volkswagen Crafter", Trim = "Volkswagen Crafter", Kind = "van" });
            await session.SaveChangesAsync();
        }
        await RavenIndexHelper.WaitForNonStaleAsync(Store);

        using var read = Store.OpenAsyncSession();
        var results = await read.Query<VCar, Cars_Search>()
            .Where(v => v.Kind == "car")
            .Search(v => v.Model, "*volkswagen*", options: SearchOptions.Guess, @operator: SearchOperator.And)
            .ProjectInto<VCar>()
            .ToListAsync();

        results.Select(v => v.Model).Should().BeEquivalentTo(["Volkswagen Golf GTI", "Volkswagen Up"]);
    }
}
