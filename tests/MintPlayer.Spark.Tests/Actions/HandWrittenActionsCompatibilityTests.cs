using System.Linq.Expressions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Actions;

/// <summary>
/// A source-compatibility fixture, not a behaviour test. It exists to fail the <em>build</em> if
/// <c>OnRefreshAsync</c> is ever moved onto <see cref="IPersistentObjectActions{T}"/>.
///
/// <para>
/// #301 already paid that cost once, deliberately, and left a note in the interface saying so:
/// declaring the row-security hooks there broke every hand-written implementer, and was worth it
/// because <c>ISparkRowRule&lt;T&gt;</c> needed reflection-free access from outside the framework.
/// Nothing outside the framework dispatches a refresh, so the same trade would be all cost. The
/// class below implements the interface by hand, exactly as an application written before this
/// feature would have; if it stops compiling, the trade has been made by accident.
/// </para>
/// </summary>
public class HandWrittenActionsCompatibilityTests
{
    public class LegacyEntity
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
    }

    /// <summary>
    /// Implements every interface member and inherits nothing. Note the absence of
    /// <c>OnRefreshAsync</c> — that absence is the whole point of the fixture.
    /// </summary>
    private sealed class LegacyHandWrittenActions : IPersistentObjectActions<LegacyEntity>
    {
        public Task<LegacyEntity?> OnLoadAsync(IAsyncDocumentSession session, string id)
            => Task.FromResult<LegacyEntity?>(null);

        public Task<LegacyEntity> OnSaveAsync(IAsyncDocumentSession session, PersistentObject obj)
            => Task.FromResult(new LegacyEntity());

        public Task OnDeleteAsync(IAsyncDocumentSession session, string id) => Task.CompletedTask;

        public Task OnBeforeSaveAsync(PersistentObject obj, LegacyEntity entity) => Task.CompletedTask;

        public Task OnAfterSaveAsync(PersistentObject obj, LegacyEntity entity) => Task.CompletedTask;

        public Task OnBeforeDeleteAsync(LegacyEntity entity) => Task.CompletedTask;

        public Task<bool> IsAllowedAsync(string action, LegacyEntity entity) => Task.FromResult(true);

        public Task<Expression<Func<LegacyEntity, bool>>?> GetRowFilterAsync(string action)
            => Task.FromResult<Expression<Func<LegacyEntity, bool>>?>(null);

        public Task<IReadOnlyCollection<string>?> GetProtectedAttributesAsync(string action, LegacyEntity entity)
            => Task.FromResult<IReadOnlyCollection<string>?>(null);
    }

    [Fact]
    public void A_hand_written_implementer_without_a_refresh_hook_still_satisfies_the_interface()
    {
        IPersistentObjectActions<LegacyEntity> actions = new LegacyHandWrittenActions();

        actions.Should().NotBeNull(
            "if this file compiles the guarantee holds; the assertion only keeps the fixture from being pruned as unused");
    }
}
