using System.Linq.Expressions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Actions;

/// <summary>
/// A contract fixture, not a behaviour test: it implements
/// <see cref="IPersistentObjectActions{T}"/> by hand, inheriting nothing, so the build fails if the
/// interface ever grows a member that cannot reasonably be implemented from outside the framework.
///
/// <para>
/// <c>OnRefreshAsync</c> is on the interface because it is a lifecycle hook and that is where
/// lifecycle hooks live. It was briefly placed off the interface to spare hand-written implementers
/// a breaking change — the cost #301 paid deliberately for the row-security hooks — but the packages
/// are in preview and the owner does not want backward compatibility bought at the price of the
/// contract being less honest than the implementation.
/// </para>
/// </summary>
public class HandWrittenActionsCompatibilityTests
{
    public class LegacyEntity
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
    }

    /// <summary>Implements every interface member and inherits nothing.</summary>
    private sealed class LegacyHandWrittenActions : IPersistentObjectActions<LegacyEntity>
    {
        public Task<PersistentObject?> OnLoadAsync(string id, PersistentObject? parent)
            => Task.FromResult<PersistentObject?>(null);

        // Added by #327 M2. A hand-written implementer owns both halves of the read contract; the
        // base class expresses the singular in terms of the plural so they cannot drift, and an
        // implementer that does not inherit it has to keep them consistent itself.
        public Task<IReadOnlyList<PersistentObject>> OnLoadManyAsync(IReadOnlyList<string> ids, PersistentObject? parent)
            => Task.FromResult<IReadOnlyList<PersistentObject>>([]);

        public Task<LegacyEntity> OnSaveAsync(IAsyncDocumentSession session, PersistentObject obj)
            => Task.FromResult(new LegacyEntity());

        public Task OnDeleteAsync(IAsyncDocumentSession session, string id) => Task.CompletedTask;

        public Task OnBeforeSaveAsync(PersistentObject obj, LegacyEntity entity) => Task.CompletedTask;

        public Task OnAfterSaveAsync(PersistentObject obj, LegacyEntity entity) => Task.CompletedTask;

        public Task OnBeforeDeleteAsync(LegacyEntity entity) => Task.CompletedTask;

        public Task OnRefreshAsync(SparkRefreshArgs<LegacyEntity> args) => Task.CompletedTask;

        public Task<bool> IsAllowedAsync(string action, LegacyEntity entity) => Task.FromResult(true);

        public Task<Expression<Func<LegacyEntity, bool>>?> GetRowFilterAsync(string action)
            => Task.FromResult<Expression<Func<LegacyEntity, bool>>?>(null);

        public Task<IReadOnlyCollection<string>?> GetProtectedAttributesAsync(string action, LegacyEntity entity)
            => Task.FromResult<IReadOnlyCollection<string>?>(null);
    }

    [Fact]
    public void The_interface_is_implementable_by_hand()
    {
        IPersistentObjectActions<LegacyEntity> actions = new LegacyHandWrittenActions();

        actions.Should().NotBeNull(
            "if this file compiles the guarantee holds; the assertion only keeps the fixture from being pruned as unused");
    }
}
