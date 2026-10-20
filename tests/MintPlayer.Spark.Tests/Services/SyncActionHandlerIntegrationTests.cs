using System.Text.Json;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// <see cref="ISyncActionHandler"/> against a real store (warehouse §4.2.5, owner decision 2026-10-20: write-back is a
/// per-property compare-and-set inside the owner's normal save path). Red on the working tree before WM1: the handler
/// applied an edit on top of the version stored NOW (<c>SyncActionHandler.cs:39-47,60</c>), so a stale replica edit
/// silently overwrote a newer owner change.
/// </summary>
public class SyncActionHandlerIntegrationTests : SparkTestDriver
{
    private static readonly Guid DocTypeId = Guid.Parse("9c0a33aa-33aa-33aa-33aa-9c0a33aa33aa");

    private SparkEndpointFactory<GuardedContext> _factory = null!;
    private ISyncActionHandler _handler = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _factory = new SparkEndpointFactory<GuardedContext>(Store, [GuardedDocModel.For(DocTypeId)]);
        _handler = _factory.GetService<ISyncActionHandler>();
    }

    public override async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private async Task<string> SeedAsync(GuardedDoc doc)
    {
        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(doc);
        await session.SaveChangesAsync();
        return doc.Id!;
    }

    private async Task<GuardedDoc?> LoadAsync(string id)
    {
        using var session = Store.OpenAsyncSession();
        return await session.LoadAsync<GuardedDoc>(id);
    }

    private static JsonElement Json(object? value) => JsonSerializer.SerializeToElement(value);

    private static SyncEdit Edit(string id, string property, object? baseValue, object? newValue) => new()
    {
        EntityType = typeof(GuardedDoc),
        DocumentId = id,
        Values = new Dictionary<string, JsonElement> { [property] = Json(newValue) },
        BaseValues = new Dictionary<string, JsonElement> { [property] = Json(baseValue) },
    };

    [Fact]
    public async Task An_edit_whose_base_is_current_is_applied()
    {
        var id = await SeedAsync(new GuardedDoc { Name = "before", IsVisible = true });

        var outcome = await _handler.ApplyEditAsync(Edit(id, "Name", "before", "after"));

        outcome.Kind.Should().Be(SyncEditOutcomeKind.Applied, outcome.Reason ?? "");
        (await LoadAsync(id))!.Name.Should().Be("after");
    }

    /// <summary>
    /// WD18 red→green: the owner changed Name after the replica was built; the edit started from the old Name. Today
    /// the edit overwrote the newer value; now it is a conflict naming Name, and the owner's value stands.
    /// </summary>
    [Fact]
    public async Task A_stale_edit_is_a_conflict_naming_the_property_and_writes_nothing()
    {
        var id = await SeedAsync(new GuardedDoc { Name = "changed-at-owner", IsVisible = true });

        var outcome = await _handler.ApplyEditAsync(Edit(id, "Name", "seen-on-replica", "edited-on-replica"));

        outcome.Kind.Should().Be(SyncEditOutcomeKind.Conflict);
        outcome.Reason.Should().Be(SparkConflictException.Changed);
        outcome.Attributes.Should().BeEquivalentTo(["Name"]);
        (await LoadAsync(id))!.Name.Should().Be("changed-at-owner");
    }

    /// <summary>An unlisted property never conflicts: the owner changed another field, this edit still applies.</summary>
    [Fact]
    public async Task A_change_of_an_unlisted_property_does_not_conflict()
    {
        var id = await SeedAsync(new GuardedDoc { Name = "same", IsVisible = true });
        using (var session = Store.OpenAsyncSession())
        {
            // The owner changes the document (a new change vector) without touching Name.
            var doc = await session.LoadAsync<GuardedDoc>(id);
            session.Advanced.GetMetadataFor(doc)["Touched"] = "yes";
            await session.SaveChangesAsync();
        }

        var outcome = await _handler.ApplyEditAsync(Edit(id, "Name", "same", "edited"));

        outcome.Kind.Should().Be(SyncEditOutcomeKind.Applied, outcome.Reason ?? "");
        (await LoadAsync(id))!.Name.Should().Be("edited");
    }

    /// <summary>No partial apply: one stale property refuses the whole edit.</summary>
    [Fact]
    public async Task One_stale_property_refuses_the_whole_edit()
    {
        var id = await SeedAsync(new GuardedDoc { Name = "owner-name", IsVisible = true });

        var outcome = await _handler.ApplyEditAsync(new SyncEdit
        {
            EntityType = typeof(GuardedDoc),
            DocumentId = id,
            Values = new Dictionary<string, JsonElement> { ["Name"] = Json("replica-name"), ["IsVisible"] = Json(true) },
            BaseValues = new Dictionary<string, JsonElement> { ["Name"] = Json("old-name"), ["IsVisible"] = Json(true) },
        });

        outcome.Kind.Should().Be(SyncEditOutcomeKind.Conflict);
        outcome.Attributes.Should().BeEquivalentTo(["Name"]);
        (await LoadAsync(id))!.Name.Should().Be("owner-name");
    }

    /// <summary>A partial edit leaves every unnamed field at its stored value.</summary>
    [Fact]
    public async Task A_partial_edit_leaves_unnamed_fields_at_their_stored_values()
    {
        var id = await SeedAsync(new GuardedDoc { Name = "before-sync", IsVisible = true });

        var outcome = await _handler.ApplyEditAsync(Edit(id, "Name", "before-sync", "after-sync"));

        outcome.Kind.Should().Be(SyncEditOutcomeKind.Applied, outcome.Reason ?? "");
        var doc = await LoadAsync(id);
        doc!.Name.Should().Be("after-sync");
        doc.IsVisible.Should().BeTrue("a partial edit must not overwrite a field it never sent");
    }

    /// <summary>#467 D15: an edit of a row deleted at the owner is a conflict (deleted), never a resurrection.</summary>
    [Fact]
    public async Task An_edit_of_a_row_deleted_here_is_a_deleted_conflict_and_not_recreated()
    {
        var id = await SeedAsync(new GuardedDoc { Name = "owned", IsVisible = true });
        using (var delete = Store.OpenAsyncSession())
        {
            delete.Delete(id);
            await delete.SaveChangesAsync();
        }

        var outcome = await _handler.ApplyEditAsync(Edit(id, "Name", "owned", "edited-on-replica"));

        outcome.Kind.Should().Be(SyncEditOutcomeKind.Conflict);
        outcome.Reason.Should().Be(SparkConflictException.Deleted);
        (await LoadAsync(id)).Should().BeNull("the owner's delete stands");
    }

    [Fact]
    public async Task An_edit_without_a_base_for_a_value_is_refused()
    {
        var id = await SeedAsync(new GuardedDoc { Name = "x", IsVisible = true });

        var outcome = await _handler.ApplyEditAsync(new SyncEdit
        {
            EntityType = typeof(GuardedDoc),
            DocumentId = id,
            Values = new Dictionary<string, JsonElement> { ["Name"] = Json("y") },
            BaseValues = new Dictionary<string, JsonElement>(),
        });

        outcome.Kind.Should().Be(SyncEditOutcomeKind.Refused);
        (await LoadAsync(id))!.Name.Should().Be("x");
    }

    /// <summary>A row the module may not edit (the row rule) is refused as "not found", uniform with the HTTP path.</summary>
    [Fact]
    public async Task An_edit_of_a_row_the_row_rule_hides_is_refused_as_forbidden()
    {
        var id = await SeedAsync(new GuardedDoc { Name = "hidden", IsVisible = false });

        var outcome = await _handler.ApplyEditAsync(Edit(id, "Name", "hidden", "edited"));

        outcome.Kind.Should().Be(SyncEditOutcomeKind.Refused);
        outcome.Refusal.Should().Be(SyncEditRefusal.Forbidden);
        (await LoadAsync(id))!.Name.Should().Be("hidden");
    }

    [Fact]
    public async Task A_delete_whose_bases_are_current_removes_the_document()
    {
        var id = await SeedAsync(new GuardedDoc { Name = "to-delete", IsVisible = true });

        var outcome = await _handler.ApplyDeleteAsync(new SyncDelete
        {
            EntityType = typeof(GuardedDoc),
            DocumentId = id,
            BaseValues = new Dictionary<string, JsonElement> { ["Name"] = Json("to-delete") },
        });

        outcome.Kind.Should().Be(SyncEditOutcomeKind.Applied, outcome.Reason ?? "");
        (await LoadAsync(id)).Should().BeNull();
    }

    [Fact]
    public async Task A_delete_of_a_row_changed_since_is_a_conflict()
    {
        var id = await SeedAsync(new GuardedDoc { Name = "renamed-at-owner", IsVisible = true });

        var outcome = await _handler.ApplyDeleteAsync(new SyncDelete
        {
            EntityType = typeof(GuardedDoc),
            DocumentId = id,
            BaseValues = new Dictionary<string, JsonElement> { ["Name"] = Json("as-seen") },
        });

        outcome.Kind.Should().Be(SyncEditOutcomeKind.Conflict);
        (await LoadAsync(id)).Should().NotBeNull();
    }

    /// <summary>The JSON comparison treats an absent stored value as equal to a null base, and nothing else.</summary>
    [Fact]
    public void Absent_and_null_compare_equal_and_values_compare_structurally()
    {
        MintPlayer.Spark.Services.SyncActionHandler.SameValue(default, Json(null)).Should().BeTrue();
        MintPlayer.Spark.Services.SyncActionHandler.SameValue(Json(new[] { 1, 2 }), Json(new[] { 1, 2 })).Should().BeTrue();
        MintPlayer.Spark.Services.SyncActionHandler.SameValue(Json(new[] { 1, 2 }), Json(new[] { 2, 1 })).Should().BeFalse();
        MintPlayer.Spark.Services.SyncActionHandler.SameValue(Json("a"), Json(null)).Should().BeFalse();
    }
}
