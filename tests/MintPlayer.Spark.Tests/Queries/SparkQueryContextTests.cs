using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Queries;
using MintPlayer.Spark.Services;
using Xunit;

namespace MintPlayer.Spark.Tests.Queries;

/// <summary>
/// The withhold mechanism's own invariants, now on the one member that knows it:
/// <see cref="IDisablable.DisableActions"/> (#460, D13).
/// <para>
/// These are cheap and they fence the property the whole design rests on: withholding is
/// <b>monotone-subtractive</b>. It can only ever remove an action, so there is no way for it to widen
/// anything. That is worth a test rather than a comment, because the obvious "improvement" someone
/// will propose is an <c>EnableActions</c>, and that would turn a refusal into a grant.
/// </para>
/// </summary>
public class DisabledActionSetTests
{
    [Fact]
    public void A_set_nobody_wrote_to_withholds_nothing()
        => new DisabledActionSet().Names.Should().BeEmpty();

    [Fact]
    public void Withholding_is_additive_across_calls()
    {
        var set = new DisabledActionSet();

        set.DisableActions("A");
        set.DisableActions("B");

        set.Names.Should().Equal("A", "B");
    }

    /// <summary>
    /// Idempotent and case-insensitive, so two concerns that each withhold the same action do not
    /// produce a list that says it twice, and a name spelled differently still matches.
    /// </summary>
    [Fact]
    public void Withholding_the_same_action_twice_records_it_once()
    {
        var set = new DisabledActionSet();

        set.DisableActions("Delete");
        set.DisableActions("delete", "DELETE");

        set.Names.Should().Equal("Delete");
    }

    [Fact]
    public void Blank_names_are_ignored_rather_than_recorded()
    {
        foreach (var names in new[] { Array.Empty<string>(), new[] { "" }, new[] { "   " } })
        {
            var set = new DisabledActionSet();

            set.DisableActions(names);

            set.Names.Should().BeEmpty();
        }
    }

    /// <summary>
    /// A persistent object records the same way through the interface — and only through it: the
    /// public <c>PersistentObject.DisableActions</c> is gone, so the hook is the one place that can.
    /// </summary>
    [Fact]
    public void A_persistent_object_withholds_only_through_the_interface()
    {
        var po = new PersistentObject { Name = "Repository", ObjectTypeId = Guid.NewGuid() };

        ((IDisablable)po).DisableActions("Resync");
        ((IDisablable)po).DisableActions("DeleteData", "RESYNC");

        po.DisabledActions.Should().Equal("Resync", "DeleteData");
        typeof(PersistentObject).GetMethods().Select(m => m.Name).Should().NotContain("DisableActions");
    }

    /// <summary>
    /// There is no way to add an action back, and this test exists to keep it that way.
    /// </summary>
    [Fact]
    public void There_is_no_way_to_enable_an_action()
    {
        var surface = typeof(IDisablable).GetMethods().Select(m => m.Name).ToArray();

        surface.Should().Equal("DisableActions");
    }

    /// <summary>The query-hook context lost its withhold members with D13.</summary>
    [Fact]
    public void The_query_hook_context_no_longer_withholds()
    {
        var surface = typeof(SparkQueryContext).GetMembers().Select(m => m.Name).ToArray();

        surface.Should().NotContain("DisableActions");
        surface.Should().NotContain("DisabledActions");
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
