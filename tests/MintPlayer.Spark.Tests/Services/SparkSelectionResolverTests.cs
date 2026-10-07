using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;
using NSubstitute;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Which right a selection's rows must pass (#467 D11/D29a), for a stored type and for a virtual one.
/// </summary>
/// <remarks>
/// A virtual type (no clrType) has rows only as its query generates them, and the grid's rule is to
/// withhold <c>Read</c> from such rows because no detail page can load them (the passkeys page's
/// <c>PasskeyRow</c>, the account page's <c>MyAccountRow</c>). Requiring <c>Read</c> there made every
/// row action on them a 404: the passkeys page's Rename and Remove never ran.
/// </remarks>
public class SparkSelectionResolverTests
{
    private readonly IQueryExecutor _queryExecutor = Substitute.For<IQueryExecutor>();
    private readonly IPermissionService _permissions = Substitute.For<IPermissionService>();

    private SparkSelectionResolver CreateResolver() => new(
        _queryExecutor,
        Substitute.For<IDatabaseAccess>(),
        _permissions,
        Substitute.For<IRowSecurity>(),
        Substitute.For<ISparkTypeResolver>(),
        Substitute.For<IAsyncDocumentSession>(),
        NullLogger<SparkSelectionResolver>.Instance,
        Substitute.For<IAttributeRightsEnforcement>());

    private static EntityTypeDefinition Type(string? clrType) => new()
    {
        Id = Guid.Parse("bc7a3a73-0000-4000-8000-000000000001"),
        Name = "Row",
        ClrType = clrType,
        Attributes = [],
    };

    private static readonly SparkQuery Query = new() { Id = Guid.NewGuid(), Name = "MyRows", Source = "Custom.MyRows", EntityType = "Row" };

    private void QueryYields(params string[] ids)
        => _queryExecutor.ExecuteQueryAsync(Query, Arg.Any<PersistentObject?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>(),
                Arg.Any<IReadOnlyCollection<string>?>(), Arg.Any<IReadOnlyList<QueryColumnFilter>?>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResult
            {
                Columns = [],
                Items = [.. ids.Select(id => new QueryResultItem { Id = id, Values = [] })],
                TotalItems = ids.Length,
                Skip = 0,
                Take = ids.Length,
            });

    private void Grant(params string[] verbs)
        => _permissions.IsAllowedAsync(Arg.Any<string>(), "Row", Arg.Any<CancellationToken>())
            .Returns(call => verbs.Contains(call.ArgAt<string>(0)));

    [Fact]
    public async Task A_virtual_rows_selection_needs_the_query_right_not_read()
    {
        QueryYields("a");
        Grant("Query");

        var rows = await CreateResolver().ResolveAsync(Type(clrType: null), Query, null, ["a"]);

        rows.Should().NotBeNull();
        rows!.Select(r => r.Id).Should().Equal("a");
    }

    [Fact]
    public async Task A_virtual_rows_selection_without_the_query_right_is_refused()
    {
        QueryYields("a");
        Grant("Read");

        (await CreateResolver().ResolveAsync(Type(clrType: null), Query, null, ["a"])).Should().BeNull();
    }

    [Fact]
    public async Task A_stored_types_selection_still_needs_read()
    {
        QueryYields("a");
        Grant("Query");

        (await CreateResolver().ResolveAsync(Type(clrType: "Some.Stored.Type"), Query, null, ["a"])).Should().BeNull();
    }
}
