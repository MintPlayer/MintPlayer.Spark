using System.Net;
using System.Net.Http.Json;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using TestModels = MintPlayer.Spark.Tests.Endpoints.PersistentObject.TestModels;

namespace MintPlayer.Spark.Tests.Endpoints.Queries;

/// <summary>
/// <c>POST /spark/queries/distinct-values</c> end to end, through <see cref="SparkClient.GetDistinctValuesAsync(Guid, string, string?, QueryColumnFilter[]?, string?, string?, CancellationToken)"/>.
/// Only the endpoint's guard had run before; the body — the executor call, search, column filters and
/// the parent gate — had not.
/// </summary>
public class DistinctValuesEndpointTests : SparkTestDriver
{
    private static readonly Guid PersonTypeId = Guid.Parse("aaaa7777-7777-7777-7777-aaaaaaaaaaaa");
    private static readonly Guid AllPeopleQueryId = Guid.Parse("bbbb7777-7777-7777-7777-bbbbbbbbbbbb");

    private SparkEndpointFactory _factory = null!;
    private SparkClient _client = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _factory = new SparkEndpointFactory(Store, [Model()]);
        _client = new SparkClient(_factory.CreateClient(), ownsClient: true);

        await SeedAsync(async session =>
        {
            await session.StoreAsync(new Person { FirstName = "Alice", LastName = "Smith" }, "people/1");
            await session.StoreAsync(new Person { FirstName = "Bob", LastName = "Smith" }, "people/2");
            await session.StoreAsync(new Person { FirstName = "Carol", LastName = "Jones" }, "people/3");
        });
    }

    public override async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private static EntityTypeFile Model()
    {
        var model = TestModels.Person(PersonTypeId);
        model.Queries = [new SparkQuery { Id = AllPeopleQueryId, Name = "AllPeople", Source = "Database.People" }];
        return model;
    }

    [Fact]
    public async Task Lists_each_value_once_ordered_by_label()
    {
        var result = await _client.GetDistinctValuesAsync(AllPeopleQueryId, "LastName");

        result.Matching.Select(v => v.Label).Should().Equal("Jones", "Smith");
        result.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task A_search_narrows_by_label()
    {
        var result = await _client.GetDistinctValuesAsync(AllPeopleQueryId, "lastname", search: " SM ");

        result.Matching.Select(v => v.Label).Should().Equal("Smith");
    }

    /// <summary>
    /// #460 M15: the grid's search narrows the ROWS the values come from, exactly as /execute
    /// narrows the rows it returns — so a searched grid's panel offers only what the grid shows.
    /// "carol" matches no LastName, so this is not the value search in disguise.
    /// </summary>
    [Fact]
    public async Task The_grid_search_narrows_the_rows_the_values_come_from()
    {
        var result = await _client.GetDistinctValuesAsync(AllPeopleQueryId, "LastName", querySearch: "carol");

        result.Matching.Select(v => v.Label).Should().Equal("Jones");
    }

    [Fact]
    public async Task The_grid_search_matches_what_execute_returns()
    {
        var rows = await _client.ExecuteQueryAsync(AllPeopleQueryId, search: "smi");
        var values = await _client.GetDistinctValuesAsync(AllPeopleQueryId, "FirstName", querySearch: "smi");

        values.Matching.Select(v => v.Label).Should().BeEquivalentTo(["Alice", "Bob"]);
        rows.TotalItems.Should().Be(2);
    }

    [Fact]
    public async Task Other_column_filters_narrow_the_rows_the_values_come_from()
    {
        var result = await _client.GetDistinctValuesAsync(AllPeopleQueryId, "LastName",
            columns:
            [
                new QueryColumnFilter { Name = "FirstName", Includes = ["Carol"] },
                new QueryColumnFilter { Name = "LastName" },
            ]);

        result.Matching.Select(v => v.Label).Should().Equal("Jones");
    }

    [Fact]
    public async Task A_column_that_is_not_on_the_query_lists_nothing()
    {
        // Indistinguishable from "nothing to list", deliberately.
        var result = await _client.GetDistinctValuesAsync(AllPeopleQueryId, "PasswordHash");

        result.Matching.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_query_is_a_404()
    {
        var ex = await Assert.ThrowsAsync<SparkClientException>(
            () => _client.GetDistinctValuesAsync(Guid.NewGuid(), "LastName"));

        ex.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_unresolvable_parent_is_a_404_not_the_unscoped_values()
    {
        var ex = await Assert.ThrowsAsync<SparkClientException>(() => _client.GetDistinctValuesAsync(
            AllPeopleQueryId, "LastName", parentId: "people/404", parentType: "Person"));

        ex.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_unknown_parent_type_is_a_404()
    {
        var ex = await Assert.ThrowsAsync<SparkClientException>(() => _client.GetDistinctValuesAsync(
            AllPeopleQueryId, "LastName", parentId: "people/1", parentType: "NoSuchType"));

        ex.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{ \"queryId\": \"bbbb7777-7777-7777-7777-bbbbbbbbbbbb\" }")]
    [InlineData("not json")]
    public async Task A_body_missing_the_query_or_the_column_is_the_same_404(string body)
    {
        using var response = await _client.SendAsync(HttpMethod.Post, "/spark/queries/distinct-values",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_caller_without_the_query_right_gets_the_same_404()
    {
        await using var denied = new SparkEndpointFactory(Store, [Model()], security: SparkTestSecurity.Empty);
        using var client = new SparkClient(denied.CreateClient(), ownsClient: true);

        var ex = await Assert.ThrowsAsync<SparkClientException>(
            () => client.GetDistinctValuesAsync(AllPeopleQueryId, "LastName"));

        ex.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
