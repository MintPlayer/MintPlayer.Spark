using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// G0 (#236) — the projection path of <see cref="RowSecurity.FilterAsync"/> must batch its
/// base-document reload.
/// <para>
/// When a query returns index projections, the row rule is written against the document, so the
/// document is what gets loaded and judged. Loading it once per row means one HTTP round-trip per
/// row, and the request session caps requests (MaxNumberOfRequestsPerSession, default 30)
/// precisely to catch that shape — so a projection-backed page over a row-scoped type threw past
/// ~29 rows. Fleet's Car/Cars_Overview/VCar is exactly this configuration; it survived E2E only
/// because those tests seed a handful of cars.
/// </para>
/// </summary>
public class RowSecurityProjectionBatchingTests : SparkTestDriver
{
    public class Note
    {
        public string? Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Owner { get; set; } = string.Empty;
    }

    /// <summary>Stands in for an index projection: carries the document id but not the field the rule needs.</summary>
    public class VNote
    {
        public string? Id { get; set; }
        public string Title { get; set; } = string.Empty;
    }

    public class NoteActions : DefaultPersistentObjectActions<Note>
    {
        public NoteActions(IEntityMapper entityMapper) : base(entityMapper) { }

        public override Task<bool> IsAllowedAsync(string action, Note entity)
            => Task.FromResult(entity.Owner == "alice");
    }

    private static RowSecurity CreateRowSecurity()
    {
        var modelLoader = Substitute.For<IModelLoader>();
        var actionsResolver = Substitute.For<IActionsResolver>();
        actionsResolver.ResolveForType(typeof(Note)).Returns(new NoteActions(new EntityMapper(modelLoader)));
        return new RowSecurity(actionsResolver);
    }

    [Fact]
    public async Task A_projection_page_beyond_the_session_request_cap_is_filtered_in_one_round_trip()
    {
        var noteIds = new List<string>();
        using (var session = Store.OpenAsyncSession())
        {
            for (var i = 0; i < 40; i++)
            {
                var note = new Note { Title = $"Note {i:D2}", Owner = i % 2 == 0 ? "alice" : "bob" };
                await session.StoreAsync(note);
                noteIds.Add(note.Id!);
            }
            await session.SaveChangesAsync();
        }

        var projections = noteIds
            .Select((id, i) => new VNote { Id = id, Title = $"Note {i:D2}" })
            .Cast<object>()
            .ToList();

        using var filterSession = Store.OpenAsyncSession();
        var visible = await CreateRowSecurity().FilterAsync(
            filterSession, projections, typeof(Note), typeof(VNote), "Query");

        visible.Should().HaveCount(20,
            "40 projected rows exceed the 30-requests-per-session cap — per-row loading throws "
            + "before the page is even judged, so the whole batch must load in one request");
        visible.Cast<VNote>().Select(v => v.Title)
            .Should().BeInAscendingOrder("filtering must preserve the query's row order");
        filterSession.Advanced.NumberOfRequests.Should().Be(1,
            "the entire page's base documents should arrive in a single batched load");
    }

    [Fact]
    public async Task A_projected_row_whose_base_document_was_deleted_is_dropped()
    {
        string keptId, deletedId;
        using (var session = Store.OpenAsyncSession())
        {
            var kept = new Note { Title = "Kept", Owner = "alice" };
            var doomed = new Note { Title = "Doomed", Owner = "alice" };
            await session.StoreAsync(kept);
            await session.StoreAsync(doomed);
            await session.SaveChangesAsync();
            keptId = kept.Id!;
            deletedId = doomed.Id!;
        }

        using (var session = Store.OpenAsyncSession())
        {
            session.Delete(deletedId);
            await session.SaveChangesAsync();
        }

        // The stale-index case: projections still name the deleted document, plus a row whose id
        // is empty. Both are unverifiable and must be dropped, not thrown.
        var projections = new List<object>
        {
            new VNote { Id = keptId, Title = "Kept" },
            new VNote { Id = deletedId, Title = "Doomed" },
            new VNote { Id = null, Title = "No id" },
        };

        using var filterSession = Store.OpenAsyncSession();
        var visible = await CreateRowSecurity().FilterAsync(
            filterSession, projections, typeof(Note), typeof(VNote), "Query");

        visible.Should().ContainSingle()
            .Which.Should().BeOfType<VNote>()
            .Which.Title.Should().Be("Kept");
    }
}
