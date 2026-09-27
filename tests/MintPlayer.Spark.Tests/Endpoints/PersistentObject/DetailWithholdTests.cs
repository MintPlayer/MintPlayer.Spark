using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Tests.Endpoints.PersistentObject;

/// <summary>
/// Withholds issued through <see cref="IClientAccessor"/> reach the detail response.
/// </summary>
/// <remarks>
/// They used not to. The accessor's withholds ride the client-operation envelope, and the detail
/// endpoint returns a bare JSON object — so an author calling <c>DisableActionsOn(po, …)</c> inside
/// <c>OnLoadAsync</c> got no error and no effect, while <c>PersistentObject.DisableActions</c> on the
/// same object worked. Two APIs that looked interchangeable, one of which did nothing on the path
/// most likely to use it.
/// </remarks>
public class DetailWithholdTests
{
    private static readonly Guid TypeId = Guid.Parse("a17e0000-0000-0000-0000-a17e00000001");

    private static Abstractions.PersistentObject Object(string id = "Docs/1") => new()
    {
        Id = id,
        Name = "Doc",
        ObjectTypeId = TypeId,
    };

    private static string[] Withheld(IClientAccessor accessor, Abstractions.PersistentObject obj)
    {
        // The endpoint's own merge, not a copy of it: a copy passes whatever the endpoint does, so
        // it could never catch the two drifting apart.
        global::MintPlayer.Spark.Endpoints.PersistentObject.GetPersistentObject.MergeClientWithholds(accessor, obj);
        return [.. obj.DisabledActions ?? []];
    }

    [Fact]
    public void A_withhold_aimed_at_this_object_applies()
    {
        var accessor = new ClientAccessor();
        var obj = Object();

        accessor.DisableActionsOn(obj, "DeleteData");

        Withheld(accessor, obj).Should().BeEquivalentTo(["DeleteData"]);
    }

    /// <summary>A withhold naming a different row must not disable the button on this one.</summary>
    [Fact]
    public void A_withhold_aimed_at_another_object_does_not()
    {
        var accessor = new ClientAccessor();

        accessor.DisableActionsOn(Object("Docs/other"), "DeleteData");

        Withheld(accessor, Object()).Should().BeEmpty();
    }

    /// <summary>This endpoint <em>is</em> the current response, so an untargeted withhold lands here.</summary>
    [Fact]
    public void An_untargeted_withhold_applies_to_the_current_response()
    {
        var accessor = new ClientAccessor();

        accessor.DisableActions("Resync");

        Withheld(accessor, Object()).Should().BeEquivalentTo(["Resync"]);
    }

    [Fact]
    public void A_session_withhold_applies()
    {
        var accessor = new ClientAccessor();

        accessor.DisableActionsForSession("Resync");

        Withheld(accessor, Object()).Should().BeEquivalentTo(["Resync"]);
    }

    /// <summary>
    /// A query withhold names a query's result view, which a detail page is not. Widening it here
    /// would disable an action somewhere its author never asked for.
    /// </summary>
    [Fact]
    public void A_query_withhold_does_not_leak_onto_a_detail_page()
    {
        var accessor = new ClientAccessor();

        accessor.DisableQueryActions("some-query", "Resync");

        Withheld(accessor, Object()).Should().BeEmpty();
    }

    /// <summary>
    /// Merging is additive and case-insensitive, so the accessor and
    /// <c>PersistentObject.DisableActions</c> converge rather than one overwriting the other.
    /// </summary>
    [Fact]
    public void Merging_keeps_what_the_object_already_withheld()
    {
        var obj = Object();
        obj.DisableActions("Resync");

        obj.DisableActions("DeleteData", "RESYNC");

        obj.DisabledActions.Should().BeEquivalentTo(["Resync", "DeleteData"]);
    }
}
