using System.Linq.Expressions;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// #460 item 1 — row policies through the real pipeline (<c>IDatabaseAccess</c>): the detail read
/// gate, the per-row <c>Can</c> flags under a non-visibility filter policy (spike S5), and the base
/// after-save WITH CHECK rerouted through row security (D1), none of which the Actions class states.
/// </summary>
public class RowPolicyPipelineTests : SparkTestDriver
{
    private static readonly Guid DocTypeId = Guid.Parse("46a1c7e0-4601-4601-4601-46a1c7e04601");

    private SparkEndpointFactory<PolicedContext> factory = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        factory = new SparkEndpointFactory<PolicedContext>(
            Store,
            [PolicedDocModel.For(DocTypeId)],
            configureSpark: spark => spark
                .AddSparkRowPolicy<PolicedSoftDeletePolicy>()
                .AddSparkRowPolicy<PolicedLockPolicy>());
    }

    public override async Task DisposeAsync()
    {
        await factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private Task SeedAsync() => SeedAsync(async session =>
    {
        await session.StoreAsync(new PolicedDoc { Id = "PolicedDocs/live", Name = "live" });
        await session.StoreAsync(new PolicedDoc { Id = "PolicedDocs/locked", Name = "locked", IsLocked = true });
        await session.StoreAsync(new PolicedDoc { Id = "PolicedDocs/deleted", Name = "deleted", IsDeleted = true });
    });

    [Fact]
    public async Task A_row_hidden_by_a_policy_is_not_readable_by_id()
    {
        await SeedAsync();
        using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IDatabaseAccess>();

        (await db.GetPersistentObjectAsync(DocTypeId, "PolicedDocs/deleted")).Should().BeNull();
    }

    [Fact]
    public async Task S5_the_per_row_can_flags_reflect_policies_the_actions_class_never_mentions()
    {
        await SeedAsync();
        using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IDatabaseAccess>();

        var live = await db.GetPersistentObjectAsync(DocTypeId, "PolicedDocs/live");
        var locked = await db.GetPersistentObjectAsync(DocTypeId, "PolicedDocs/locked");

        live!.Can.Should().NotBeNull("a type governed by a policy has a row rule, so the flags are computed");
        live.Can!.Edit.Should().BeTrue();
        live.Can.Delete.Should().BeTrue();
        locked!.Can!.Edit.Should().BeFalse("the lock check policy refuses Edit");
        locked.Can.Delete.Should().BeTrue();
    }

    [Fact]
    public async Task The_after_save_WITH_CHECK_applies_policies()
    {
        using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IDatabaseAccess>();
        var po = new PersistentObject { ObjectTypeId = DocTypeId, Name = "PolicedDoc" };
        po.AddAttribute(new PersistentObjectAttribute { Name = "Name", DataType = "string", Value = "born deleted", IsValueChanged = true });
        po.AddAttribute(new PersistentObjectAttribute { Name = "IsDeleted", DataType = "bool", Value = true, IsValueChanged = true });

        var act = () => db.SavePersistentObjectAsync(po);

        await act.Should().ThrowAsync<SparkRowLevelAccessDeniedException>(
            "a create must produce a row its caller could see — policies included");
        using var verify = Store.OpenAsyncSession();
        (await verify.Query<PolicedDoc>().Customize(c => c.WaitForNonStaleResults()).CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task An_edit_WITH_CHECK_refuses_leaves_nothing_for_a_later_save_in_the_request()
    {
        // #460, M7 finding: the base OnSaveAsync loads the row into the REQUEST session and maps the
        // posted values onto it before WITH CHECK judges the result. A refusal must not leave that
        // modified entity tracked, or the request's next SaveChangesAsync writes the refused change.
        await SeedAsync();
        using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IDatabaseAccess>();
        var po = new PersistentObject { ObjectTypeId = DocTypeId, Name = "PolicedDoc", Id = "PolicedDocs/live" };
        po.AddAttribute(new PersistentObjectAttribute { Name = "Name", DataType = "string", Value = "renamed", IsValueChanged = true });
        po.AddAttribute(new PersistentObjectAttribute { Name = "IsDeleted", DataType = "bool", Value = true, IsValueChanged = true });

        var act = () => db.SavePersistentObjectAsync(po);
        await act.Should().ThrowAsync<SparkRowLevelAccessDeniedException>("the edit moves the row out of the caller's scope");

        // Any later write in the same request commits the request session.
        await scope.ServiceProvider.GetRequiredService<Raven.Client.Documents.Session.IAsyncDocumentSession>().SaveChangesAsync();

        using var verify = Store.OpenAsyncSession();
        var stored = await verify.LoadAsync<PolicedDoc>("PolicedDocs/live");
        stored.Name.Should().Be("live", "a refused save must leave nothing behind");
        stored.IsDeleted.Should().BeFalse();
    }
}

public class PolicedDoc
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsDeleted { get; set; }
    public bool IsLocked { get; set; }
}

public sealed class PolicedSoftDeletePolicy : RowFilterPolicy<PolicedDoc>
{
    public override ValueTask<Expression<Func<PolicedDoc, bool>>?> GetFilterAsync(RowPolicyContext context)
        => ValueTask.FromResult<Expression<Func<PolicedDoc, bool>>?>(d => d.IsDeleted != true);
}

public sealed class PolicedLockPolicy : RowCheckPolicy<PolicedDoc>
{
    public override ValueTask<bool> IsAllowedAsync(RowPolicyContext context, PolicedDoc entity)
        => ValueTask.FromResult(context.Action != "Edit" || !entity.IsLocked);
}

public class PolicedContext : SparkContext
{
    public IRavenQueryable<PolicedDoc> Docs => Session.Query<PolicedDoc>();
}

public static class PolicedDocModel
{
    public static EntityTypeFile For(Guid id) => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = id,
            Name = "PolicedDoc",
            ClrType = typeof(PolicedDoc).FullName!,
            Breadcrumb = "{Name}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Name", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "IsDeleted", DataType = "bool" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "IsLocked", DataType = "bool" },
            ],
        }
    };
}
