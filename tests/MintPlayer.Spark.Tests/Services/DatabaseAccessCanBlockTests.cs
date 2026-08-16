using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// #243 — the per-row <c>can</c> block must never claim more than the caller's type-level
/// rights (<c>GET /spark/permissions/{type}</c>). The row rule can only <em>narrow</em> them.
/// <para>
/// The factory here does NOT run under the permissive test default: <see cref="IAccessControl"/>
/// is replaced with a Read/Query-only double, reproducing the documented anonymous-read pattern
/// (grant <c>QueryRead</c> to Everyone + a row filter) that surfaced the overclaim on Coverage.
/// The restrictive direction — a row rule denying what type-level allows — is pinned by
/// <see cref="DatabaseAccessRowLevelAuthzTests.Get_attaches_the_per_row_can_block_for_a_row_scoped_type"/>
/// and must not regress.
/// </para>
/// </summary>
public class DatabaseAccessCanBlockTests : SparkTestDriver
{
    private static readonly Guid DocTypeId = Guid.Parse("243a0001-2430-2430-2430-243a00012430");
    private static readonly Guid PlainTypeId = Guid.Parse("243a0002-2430-2430-2430-243a00022430");

    private SparkEndpointFactory<GuardedContext> _factory = null!;
    private IDatabaseAccess _dbAccess = null!;

    /// <summary>Allows <c>Read/*</c> and <c>Query/*</c>, denies every write — the QueryRead-only
    /// grant from the issue, expressed at the IAccessControl layer so the real
    /// <c>PermissionService</c> action-string composition stays under test.</summary>
    private sealed class ReadOnlyAccessControl : IAccessControl
    {
        public Task<bool> IsAllowedAsync(string resource, CancellationToken cancellationToken = default)
            => Task.FromResult(resource.StartsWith("Read/") || resource.StartsWith("Query/"));
    }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _factory = new SparkEndpointFactory<GuardedContext>(
            Store,
            [GuardedDocModel.For(DocTypeId), PlainDocModel.For(PlainTypeId)],
            configureServices: services =>
            {
                services.RemoveAll<IAccessControl>();
                services.AddSingleton<IAccessControl, ReadOnlyAccessControl>();
            });
        _dbAccess = _factory.GetService<IDatabaseAccess>();
    }

    public override async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private Task SeedAsync(params object[] docs)
        => base.SeedAsync(async session =>
        {
            foreach (var d in docs) await session.StoreAsync(d);
        });

    [Fact]
    public async Task Can_block_never_claims_more_than_type_level_rights()
    {
        // The #243 repro: the row rule allows every action on a visible row, but the caller's
        // type-level grant is QueryRead-only — so the truthful answer is "readable, not writable".
        await SeedAsync(new GuardedDoc { Id = "docs/public", Name = "public", IsVisible = true });

        var result = await _dbAccess.GetPersistentObjectAsync(DocTypeId, "docs/public");

        result.Should().NotBeNull("Read is granted at type level and the row is visible");
        result!.Can.Should().NotBeNull("the type has a row rule, so the block is emitted");
        result.Can!.Edit.Should().BeFalse(
            "no type-level Edit right exists — the row rule must not widen what "
            + "GET /spark/permissions/{type} would report");
        result.Can.Delete.Should().BeFalse("same for Delete");
    }

    [Fact]
    public async Task A_type_without_a_row_rule_leaves_the_can_block_null()
    {
        // Absent means "no per-row information; use type-level permissions" — the client contract
        // since #236 G5, previously asserted only in a comment.
        await SeedAsync(new PlainDoc { Id = "plainDocs/1", Name = "plain" });

        var result = await _dbAccess.GetPersistentObjectAsync(PlainTypeId, "plainDocs/1");

        result.Should().NotBeNull();
        result!.Can.Should().BeNull();
    }
}
