using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Edit and delete of a row that <c>GetRowFilterAsync</c> hides, through <see cref="IDatabaseAccess"/>.
/// <para>
/// <b>These are the regression detectors for row security.</b> Everything else in the suite covers
/// the <em>list</em>: pushdown, projection fallback, redaction, the <c>can</c> block. If someone
/// ever "scopes rows" in <c>OnQueryAsync</c> and deletes their <c>GetRowFilterAsync</c> override,
/// every one of those tests still passes — the list is the one thing that misuse gets right — while
/// edit, delete and detail-by-id fall wide open. These two fail, which is the entire point of
/// writing them.
/// </para>
/// </summary>
/// <remarks>
/// They go through <see cref="IDatabaseAccess"/> deliberately. An earlier draft called
/// <c>OnSaveAsync</c>/<c>OnDeleteAsync</c> on the actions class and both passed when they should
/// have failed: the write gates are <b>not</b> in the actions base. <c>DatabaseAccess</c> checks
/// row security and then delegates, so testing the actions class directly tests the half that never
/// had the guard. That asymmetry is easy to walk into and worth stating here.
/// </remarks>
public class RowFilterWritePathTests : SparkTestDriver
{
    private static readonly Guid DocTypeId = Guid.Parse("5c1d77bb-77bb-77bb-77bb-5c1d77bb77bb");

    private SparkEndpointFactory<GuardedContext> _factory = null!;
    private IDatabaseAccess _dbAccess = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _factory = new SparkEndpointFactory<GuardedContext>(Store, new[] { FilteredDocModel.For(DocTypeId) });
        _dbAccess = _factory.GetService<IDatabaseAccess>();
    }

    public override async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private Task SeedBobsDocAsync() => SeedAsync(async session =>
        await session.StoreAsync(new FilteredDoc { Id = "FilteredDocs/bob-1", Name = "Bob's doc", Owner = "bob" }));

    /// <summary>
    /// A row the filter hides is not readable by id. Null, not a refusal — existence must not leak
    /// (security audit M-3), so the endpoint answers 404 exactly as it would for a missing document.
    /// </summary>
    [Fact]
    public async Task A_row_the_filter_hides_is_not_readable_by_id()
    {
        await SeedBobsDocAsync();

        var result = await _dbAccess.GetPersistentObjectAsync(DocTypeId, "FilteredDocs/bob-1");

        result.Should().BeNull("a filter-scoped type must gate the detail read, not only the list");
    }

    /// <summary>
    /// Deleting it is refused, and — asserted separately — the document survives. A delete that
    /// threw <em>after</em> removing the row would satisfy a throw-only assertion having already
    /// destroyed the data.
    /// </summary>
    [Fact]
    public async Task Deleting_a_row_the_filter_hides_is_refused_and_the_document_survives()
    {
        await SeedBobsDocAsync();

        var act = () => _dbAccess.DeletePersistentObjectAsync(DocTypeId, "FilteredDocs/bob-1");

        await act.Should().ThrowAsync<SparkRowLevelAccessDeniedException>(
            "a row the caller cannot see must not be deletable by id");

        using var verify = Store.OpenAsyncSession();
        (await verify.LoadAsync<FilteredDoc>("FilteredDocs/bob-1"))
            .Should().NotBeNull("the refusal must happen before the delete, not after it");
    }
}
