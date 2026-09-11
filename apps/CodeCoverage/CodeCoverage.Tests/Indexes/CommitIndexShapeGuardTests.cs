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

    [Fact]
    public void Commits_ByRepository_stores_no_fields()
    {
        var definition = new Commits_ByRepository().CreateIndexDefinition();

        definition.Fields.Values
            .Any(f => f.Storage == FieldStorage.Yes)
            .Should().BeFalse(
                "storing a field makes a projection read from the index rather than the document, " +
                "which is exactly what destroys a DateTimeOffset's offset");
    }

    /// <summary>
    /// The claim the two tests above rest on. If a <c>DateTimeOffset</c> ever appears on another
    /// entity, that entity needs checking too — most likely it has a generated index and therefore
    /// needs wrapper companions.
    /// </summary>
    [Fact]
    public void Commit_is_still_the_only_entity_carrying_a_DateTimeOffset()
    {
        var offenders = typeof(Commit).Assembly.GetTypes()
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
