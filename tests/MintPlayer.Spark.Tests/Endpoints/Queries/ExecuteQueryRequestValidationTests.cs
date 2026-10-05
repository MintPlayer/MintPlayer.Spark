using System.Net;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using TestModels = MintPlayer.Spark.Tests.Endpoints.PersistentObject.TestModels;

namespace MintPlayer.Spark.Tests.Endpoints.Queries;

/// <summary>
/// The request-shape refusals of <c>POST /spark/queries/execute</c>: a missing query id, sort and
/// filter columns outside the allow-list, and a parent that does not resolve. Each happens after
/// the query right is checked, so none of them can be used to enumerate attribute names.
/// </summary>
public class ExecuteQueryRequestValidationTests(ExecuteQueryRequestValidationTests.Host host)
    : SparkSharedTestDriver(host), IClassFixture<ExecuteQueryRequestValidationTests.Host>, IDisposable
{
    private static readonly Guid PersonTypeId = Guid.Parse("aaaa8888-8888-8888-8888-aaaaaaaaaaaa");
    private static readonly Guid AllPeopleQueryId = Guid.Parse("bbbb8888-8888-8888-8888-bbbbbbbbbbbb");

    /// <summary>
    /// One host and one seed for the class (M8 item 5). Every case seeded the same two people and
    /// then only read, so the seed moved to class setup and the cases share it.
    /// </summary>
    public sealed class Host : SharedSparkHost<TestSparkContext>
    {
        protected override SparkEndpointFactory<TestSparkContext> CreateFactory()
            => new SparkEndpointFactory(Store, [Model()]);

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();

            using var session = Store.OpenAsyncSession();
            session.Advanced.WaitForIndexesAfterSaveChanges(RavenIndexingExtensions.DefaultTimeout, throwOnTimeout: true);
            await session.StoreAsync(new Person { FirstName = "Alice", LastName = "Smith" }, "people/1");
            await session.StoreAsync(new Person { FirstName = "Bob", LastName = "Jones" }, "people/2");
            await session.SaveChangesAsync();
        }
    }

    private readonly SparkClient _client = new(host.Factory.CreateClient(), ownsClient: true);

    public void Dispose() => _client.Dispose();

    private static EntityTypeFile Model()
    {
        var model = TestModels.Person(PersonTypeId);
        model.Queries =
        [
            new SparkQuery
            {
                Id = AllPeopleQueryId,
                Name = "AllPeople",
                Source = "Database.People",
                // Declared by the query itself, so allowed even though it is not an attribute.
                SortColumns = [new SortColumn { Property = "Id" }],
            },
        ];
        return model;
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{ \"queryId\": \"\" }")]
    [InlineData("null")]
    public async Task A_body_without_a_query_id_is_the_uniform_404(string body)
    {
        using var response = await _client.SendAsync(HttpMethod.Post, "/spark/queries/execute",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_unknown_sort_column_is_a_400_naming_it()
    {
        var ex = (await new Func<Task>(() => _client.ExecuteQueryAsync(
            AllPeopleQueryId, sortColumns: [new SortColumn { Property = "PasswordHash" }])).Should().ThrowExactlyAsync<SparkClientException>()).Which;

        ex.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        ex.ResponseBody.Should().Contain("Unknown sort column(s): PasswordHash");
    }

    /// <summary>
    /// A misspelled direction used to sort ascending without a word, so a caller asking for the
    /// newest first silently got the oldest first (PRD F5/D5, owner: "the Spark frontend (and any other
    /// caller) must post a correct request, otherwise these failures become silent").
    /// </summary>
    [Theory]
    [InlineData("dsc")]
    [InlineData("descending")]
    [InlineData("")]
    public async Task An_unknown_sort_direction_is_a_400_naming_it(string direction)
    {
        var ex = (await new Func<Task>(() => _client.ExecuteQueryAsync(AllPeopleQueryId,
            sortColumns: [new SortColumn { Property = "lastname", Direction = direction }])).Should().ThrowExactlyAsync<SparkClientException>()).Which;

        ex.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        ex.ResponseBody.Should().Contain($"Unknown sort direction '{direction}' for lastname");
    }

    [Theory]
    [InlineData("asc")]
    [InlineData("ASC")]
    [InlineData("desc")]
    [InlineData("Desc")]
    public async Task Both_sort_directions_are_accepted_in_any_case(string direction)
    {
        var result = await _client.ExecuteQueryAsync(AllPeopleQueryId,
            sortColumns: [new SortColumn { Property = "lastname", Direction = direction }]);

        result.TotalItems.Should().Be(2);
    }

    [Fact]
    public async Task Attribute_and_declared_sort_columns_are_allowed()
    {
        var byName = await _client.ExecuteQueryAsync(AllPeopleQueryId,
            sortColumns: [new SortColumn { Property = "lastname", Direction = "desc" }]);
        var byDeclared = await _client.ExecuteQueryAsync(AllPeopleQueryId,
            sortColumns: [new SortColumn { Property = "Id" }]);

        byName.TotalItems.Should().Be(2);
        byDeclared.TotalItems.Should().Be(2);
    }

    [Fact]
    public async Task An_unknown_filter_column_is_a_400_naming_it()
    {
        var ex = (await new Func<Task>(() => _client.ExecuteQueryAsync(
            AllPeopleQueryId, columns: [new QueryColumnFilter { Name = "Salary", Includes = [1] }])).Should().ThrowExactlyAsync<SparkClientException>()).Which;

        ex.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        ex.ResponseBody.Should().Contain("Unknown filter column(s): Salary");
    }

    [Fact]
    public async Task An_empty_filter_is_ignored_even_on_an_unknown_column()
    {
        // Only a filter that says something is validated; an empty one is dropped first.
        var result = await _client.ExecuteQueryAsync(AllPeopleQueryId,
            columns: [new QueryColumnFilter { Name = "Salary" }]);

        result.TotalItems.Should().Be(2);
    }

    [Fact]
    public async Task A_parent_that_does_not_resolve_is_a_404_rather_than_the_unscoped_rows()
    {
        var ex = (await new Func<Task>(() => _client.ExecuteQueryAsync(
            AllPeopleQueryId, parentId: "people/404", parentType: "Person")).Should().ThrowExactlyAsync<SparkClientException>()).Which;

        ex.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_parent_of_an_unknown_type_is_a_404()
    {
        var ex = (await new Func<Task>(() => _client.ExecuteQueryAsync(
            AllPeopleQueryId, parentId: "people/1", parentType: "NoSuchType")).Should().ThrowExactlyAsync<SparkClientException>()).Which;

        ex.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Sort_validation_is_not_reachable_without_the_query_right()
    {
        // Otherwise 400-vs-404 would tell an unauthorized caller which attribute names exist.
        await using var denied = new SparkEndpointFactory(Store, [Model()], security: SparkTestSecurity.Empty);
        using var client = new SparkClient(denied.CreateClient(), ownsClient: true);

        var ex = (await new Func<Task>(() => client.ExecuteQueryAsync(
            AllPeopleQueryId, sortColumns: [new SortColumn { Property = "PasswordHash" }])).Should().ThrowExactlyAsync<SparkClientException>()).Which;

        ex.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
