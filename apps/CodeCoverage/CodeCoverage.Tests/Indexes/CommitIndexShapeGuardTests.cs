using System.Reflection;
using CodeCoverage.Indexes;
using CodeCoverage.Entities;
using MintPlayer.Spark.Abstractions;
using Raven.Client.Documents.Indexes;
using Xunit;

namespace CodeCoverage.Tests.Indexes;

/// <summary>
/// Guards the property that makes this app's <see cref="DateTimeOffset"/> data safe — which is
/// currently the absence of one line, not a design decision anyone wrote down.
/// </summary>
/// <remarks>
/// <para>
/// RavenDB destroys a <c>DateTimeOffset</c>'s offset when the value becomes a <em>stored scalar index
/// field</em> read back through a projection. <see cref="Commit"/> is the only entity in this app
/// carrying a <c>DateTimeOffset</c> (<c>AuthoredAt</c>, <c>FirstSeenAtUtc</c>, <c>Date</c>) — and the
/// only one without a generated index, so the trigger and the type never meet. Every generated index
/// emits <c>StoreAllFields</c> unconditionally; <see cref="Commits_ByRepository"/> stores nothing.
/// </para>
/// <para>
/// Those offsets are real data, not incidental: a git author timestamp carries the committer's own
/// timezone and arrives that way from the GitHub webhook, and it is displayed — via
/// <c>BrowseController</c>'s commit list and the trend chart. Adding <c>[GenerateIndex]</c> to
/// <c>Commit</c>, or a <c>StoreAllFields</c> / <c>Store(...)</c> to this index, would silently start
/// flattening them. Nothing would fail: ordering, filtering and row counts all stay correct, because
/// the instant is preserved.
/// </para>
/// <para>
/// If you are here because this test failed, the fix is not to delete it. Add the
/// <c>{Name}Raw</c> wrapper companions so the offsets survive the projection — see
/// <c>docs/guide-dates-and-sorting.md</c>.
/// </para>
/// </remarks>
public class CommitIndexShapeGuardTests
{
    [Fact]
    public void Commit_has_no_generated_index()
    {
        typeof(Commit).GetCustomAttributes()
            .Any(a => a.GetType().Name == "GenerateIndexAttribute")
            .Should().BeFalse(
                "a generated index emits StoreAllFields unconditionally, which would flatten every " +
                "DateTimeOffset on Commit the moment it is projected");
    }

    /// <summary>
    /// ⚠️ <b>Widened, deliberately, from "stores nothing" to "stores nothing that would lose an
    /// offset".</b>
    /// </summary>
    /// <remarks>
    /// The old assertion was "no field has <c>FieldStorage.Yes</c>", which was a proxy for the real
    /// invariant and stopped being true the moment this index gained a <c>[FromIndex]</c> projection:
    /// the three fields computed in the map (<c>HasCoverage</c>, <c>ParentLookupDone</c>,
    /// <c>CompleteCoverage</c>) exist nowhere on the document, so without storing them they project as
    /// null.
    /// <para>
    /// The invariant that actually protects the data is narrower and measured: <b>a projection
    /// resolves per field</b> — stored fields come from the index, unstored ones from the document —
    /// so what must never be stored is a <em>scalar</em> <c>DateTimeOffset</c>. Unstored gives back
    /// <c>+02:00</c>, <c>-05:00</c> and <c>+05:30</c> exactly; stored gives back <c>…Z</c>.
    /// </para>
    /// <para>
    /// The allow-list is the point: it is what stops a future <c>StoreAllFields</c> from quietly
    /// putting <c>Date</c> back. If you are here because this test failed, do not widen the list
    /// without checking what the new field is.
    /// </para>
    /// </remarks>
    [Fact]
    public void Commits_ByRepository_stores_only_what_the_document_cannot_answer_for()
    {
        var definition = new Commits_ByRepository().CreateIndexDefinition();

        var stored = definition.Fields
            .Where(f => f.Value.Storage == FieldStorage.Yes)
            .Select(f => f.Key)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        string.Join(",", stored).Should().Be(
            $"{nameof(VCommit.CompleteCoverage)},{nameof(VCommit.DateRaw)}," +
            $"{nameof(VCommit.HasCoverage)},{nameof(VCommit.ParentLookupDone)}",
            "only map-computed fields and the offset-preserving wrapper may be stored; everything " +
            "else is read back from the document, which is what keeps Date's offset");
    }

    /// <summary>
    /// No scalar <see cref="DateTimeOffset"/> on the projection is stored, and each one has a stored
    /// wrapper to recover it from.
    /// </summary>
    /// <remarks>
    /// Stated structurally rather than as a list of names, so it keeps holding when someone adds a
    /// second date to the projection — which is exactly the change that would otherwise reintroduce
    /// the defect.
    /// </remarks>
    [Fact]
    public void No_scalar_DateTimeOffset_is_stored_and_each_one_has_a_stored_wrapper()
    {
        var definition = new Commits_ByRepository().CreateIndexDefinition();

        var dates = typeof(VCommit).GetProperties()
            .Where(p => (Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType) == typeof(DateTimeOffset))
            .Select(p => p.Name)
            .ToArray();

        dates.Should().NotBeEmpty("otherwise this test passes vacuously and guards nothing");

        foreach (var date in dates)
        {
            definition.Fields.TryGetValue(date, out var field);
            (field?.Storage == FieldStorage.Yes).Should().BeFalse(
                $"storing '{date}' as a scalar flattens it to UTC and destroys the offset");

            definition.Fields.TryGetValue(date + "Raw", out var wrapper);
            (wrapper?.Storage == FieldStorage.Yes).Should().BeTrue(
                $"'{date}' needs its stored {date}Raw wrapper, or a document written before the " +
                "field existed has nothing to recover the value from");
        }
    }

    /// <summary>
    /// The claim the two tests above rest on. If a <c>DateTimeOffset</c> ever appears on another
    /// entity, that entity needs checking too — most likely it has a generated index and therefore
    /// needs wrapper companions.
    /// <para>
    /// ⚠️ Scoped to the entity namespace, deliberately. It used to scan the whole assembly, which
    /// made it fire on <c>BranchCommitPushed.AuthoredAt</c> — a bus message payload that is never a
    /// document, never indexed and never projected. That is a false positive, and the tempting
    /// "fix" of changing the message to <c>DateTime</c> would have dropped the offset in transit:
    /// precisely the class of silent loss this guard was written to catch.
    /// </para>
    /// </summary>
    [Fact]
    public void Commit_is_still_the_only_entity_carrying_a_DateTimeOffset()
    {
        var offenders = typeof(Commit).Assembly.GetTypes()
            // Entities only. The defect is an index projection flattening the offset, so it can
            // only reach a type that becomes a Raven document and gets indexed. A bus message
            // payload lives inside SparkMessage's JSON, is never indexed and is never projected, so
            // a DateTimeOffset on one is a different risk class — and forcing it to DateTime would
            // drop the offset in transit, which is the very thing this guard exists to prevent.
            .Where(t => t.Namespace == typeof(Commit).Namespace)
            .Where(t => t.IsClass && !t.IsAbstract && t != typeof(Commit))
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => (Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType) == typeof(DateTimeOffset))
                .Select(p => $"{t.Name}.{p.Name}"))
            .OrderBy(x => x)
            .ToList();

        offenders.Should().BeEmpty(
            "every other temporal field in this app is a plain DateTime, which the defect does not " +
            "touch; a new DateTimeOffset needs its index shape checked");
    }
}
