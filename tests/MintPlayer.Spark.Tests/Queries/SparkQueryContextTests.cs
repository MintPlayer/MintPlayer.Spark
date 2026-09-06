using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Queries;
using Xunit;

namespace MintPlayer.Spark.Tests.Queries;

/// <summary>
/// The withhold mechanism's own invariants.
/// <para>
/// These are cheap and they fence the property the whole design rests on: withholding is
/// <b>monotone-subtractive</b>. It can only ever remove an action from a catalogue the server
/// already decided the caller may see, so — unlike the per-row <c>can</c> block, which is an
/// intersection that has to be maintained — there is no way for it to widen anything. That is
/// worth a test rather than a comment, because the obvious "improvement" someone will propose is
/// an <c>EnableActions</c>, and that would turn an affordance into a permission.
/// </para>
/// </summary>
public class SparkQueryContextTests
{
    private static SparkQueryContext AContext() => new()
    {
        Query = new SparkQueryInfo
        {
            Id = Guid.NewGuid(),
            Name = "Repositories",
            Source = "Database.Repositories",
            SortColumns = [],
        },
    };

    [Fact]
    public void A_context_nobody_wrote_to_withholds_nothing()
        => Assert.Null(AContext().DisabledActions);

    [Fact]
    public void Withholding_is_additive_across_calls()
    {
        var context = AContext();

        context.DisableActions("A");
        context.DisableActions("B");

        Assert.Equal(["A", "B"], context.DisabledActions);
    }

    /// <summary>
    /// Idempotent and case-insensitive, so two concerns that each withhold the same action do not
    /// produce a list that says it twice, and a name spelled differently still matches the
    /// catalogue entry the client filters against.
    /// </summary>
    [Fact]
    public void Withholding_the_same_action_twice_records_it_once()
    {
        var context = AContext();

        context.DisableActions("Delete");
        context.DisableActions("delete", "DELETE");

        Assert.Equal(["Delete"], context.DisabledActions);
    }

    [Fact]
    public void Blank_names_are_ignored_rather_than_recorded()
    {
        foreach (var names in new[] { Array.Empty<string>(), new[] { "" }, new[] { "   " } })
        {
            var context = AContext();

            context.DisableActions(names);

            Assert.True(context.DisabledActions is null || context.DisabledActions.Count == 0);
        }
    }

    /// <summary>
    /// There is no way to add an action, and this test exists to keep it that way. The mechanism is
    /// an affordance over a catalogue the server already authorised; a name that is not in that
    /// catalogue is a no-op on the client, and no API here can put one there.
    /// </summary>
    [Fact]
    public void There_is_no_way_to_enable_an_action()
    {
        var surface = typeof(SparkQueryContext)
            .GetMethods()
            .Select(m => m.Name)
            .ToArray();

        Assert.DoesNotContain("EnableActions", surface);
        Assert.DoesNotContain("AllowActions", surface);
        Assert.Contains("DisableActions", surface);
    }
}

/// <summary>
/// <see cref="SparkQueryInfo"/> exists because handing a hook the real <see cref="SparkQuery"/> was
/// unsafe: <c>ModelLoader</c> is a singleton and returns its cached queries by reference, and every
/// property on <c>SparkQuery</c> has a public setter — so a hook could have rewritten a query for
/// every subsequent request, for every user, until the process restarted.
/// <para>
/// An earlier revision shipped exactly that, with a comment claiming the property was read-only.
/// These tests are the version the compiler cannot express on its own.
/// </para>
/// </summary>
public class SparkQueryInfoTests
{
    [Fact]
    public void Every_property_is_init_only()
    {
        var settable = typeof(SparkQueryInfo)
            .GetProperties()
            .Where(p => p.SetMethod is { } setter && !setter.ReturnParameter
                .GetRequiredCustomModifiers()
                .Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit"))
            .Select(p => p.Name)
            .ToArray();

        Assert.Empty(settable);
    }

    /// <summary>
    /// The sort columns are COPIED, not aliased. <c>SparkQuery.WithSortColumns</c> is a
    /// MemberwiseClone plus a reassignment, so it shares the original array — meaning even a
    /// per-request clone of the query would let <c>context.Query.SortColumns[0] = …</c> reach the
    /// singleton. Copying here is what actually severs that.
    /// </summary>
    [Fact]
    public void The_sort_columns_are_copied_from_the_query()
    {
        var query = new SparkQuery
        {
            Id = Guid.NewGuid(),
            Name = "Repositories",
            Source = "Database.Repositories",
            SortColumns = [new SortColumn { Property = "Name", Direction = "asc" }],
        };

        var info = SparkQueryInfo.From(query);

        Assert.NotSame(query.SortColumns, info.SortColumns);
        Assert.Single(info.SortColumns);
    }
}
