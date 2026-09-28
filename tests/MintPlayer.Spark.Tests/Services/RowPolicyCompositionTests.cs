using System.Linq.Expressions;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using NSubstitute;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// #460 item 1 — row policies composed inside <see cref="RowSecurity"/>: AND with the Actions class's
/// rule by parameter rebinding, constant folding, the system-context opt-out, the (type, action) memo,
/// and the <see cref="RowRuleKinds"/> split of spike S5.
/// </summary>
public class RowPolicyCompositionTests : SparkTestDriver
{
    public interface IRpSoftDeletable
    {
        bool IsDeleted { get; set; }
    }

    public class RpCar : IRpSoftDeletable
    {
        public string? Id { get; set; }
        public string LicensePlate { get; set; } = string.Empty;
        public bool IsDeleted { get; set; }
    }

    public class RpTag : IRpSoftDeletable
    {
        public string? Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool IsDeleted { get; set; }
    }

    public class RpPlain
    {
        public string? Id { get; set; }
    }

    public class RpCarActions(IEntityMapper entityMapper) : DefaultPersistentObjectActions<RpCar>(entityMapper)
    {
        public override Task<Expression<Func<RpCar, bool>>?> GetRowFilterAsync(string action)
            => Task.FromResult<Expression<Func<RpCar, bool>>?>(c => c.LicensePlate != "HIDDEN");
    }

    public sealed class SoftDeletePolicy : RowFilterPolicy<IRpSoftDeletable>
    {
        public readonly Dictionary<(Type, string), int> Calls = [];
        public RowPolicyContext? LastContext;

        public override bool BypassInSystemContext => false;

        public override ValueTask<Expression<Func<IRpSoftDeletable, bool>>?> GetFilterAsync(RowPolicyContext context)
        {
            Calls[(context.EntityType, context.Action)] = Calls.GetValueOrDefault((context.EntityType, context.Action)) + 1;
            LastContext = context;
            return ValueTask.FromResult<Expression<Func<IRpSoftDeletable, bool>>?>(
                context.Deleted == SparkDeletedFilter.Include ? null : x => x.IsDeleted != true);
        }
    }

    /// <summary>A visibility decision for viewers; bypassed for the system.</summary>
    public sealed class TenantPolicy : RowFilterPolicy<RpTag>
    {
        public static bool DenyAll;
        public override bool IsVisibilityDecision => true;

        public override ValueTask<Expression<Func<RpTag, bool>>?> GetFilterAsync(RowPolicyContext context)
            => ValueTask.FromResult<Expression<Func<RpTag, bool>>?>(DenyAll ? t => false : t => true);
    }

    public sealed class LockPolicy : RowCheckPolicy<RpCar>
    {
        public override ValueTask<bool> IsAllowedAsync(RowPolicyContext context, RpCar entity)
            => ValueTask.FromResult(context.Action != "Edit" || entity.LicensePlate != "LOCKED");
    }

    private static RowSecurity Build(IRowPolicy[] policies, IHttpContextAccessor? accessor = null, SparkDeletedFilter deleted = SparkDeletedFilter.Exclude)
    {
        var mapper = new EntityMapper(Substitute.For<IModelLoader>());
        var resolver = Substitute.For<IActionsResolver>();
        resolver.ResolveForType(typeof(RpCar)).Returns(new RpCarActions(mapper));
        resolver.ResolveForType(typeof(RpTag)).Returns(new DefaultPersistentObjectActions<RpTag>(mapper));
        resolver.ResolveForType(typeof(RpPlain)).Returns(new DefaultPersistentObjectActions<RpPlain>(mapper));
        var state = new RowPolicyRequestState { Deleted = deleted };
        return new RowSecurity(resolver, httpContextAccessor: accessor, rowPolicies: policies, requestState: state);
    }

    private static IHttpContextAccessor SystemContext() => new HttpContextAccessor
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(SparkSystemContext.ClaimType, "test")], "test")),
        },
    };

    private async Task SeedCarsAsync() => await SeedAsync(async session =>
    {
        await session.StoreAsync(new RpCar { Id = "RpCars/1", LicensePlate = "A" });
        await session.StoreAsync(new RpCar { Id = "RpCars/2", LicensePlate = "B", IsDeleted = true });
        await session.StoreAsync(new RpCar { Id = "RpCars/3", LicensePlate = "HIDDEN" });
        await session.StoreAsync(new RpCar { Id = "RpCars/4", LicensePlate = "LOCKED" });
    });

    [Fact]
    public async Task The_actions_filter_and_a_policy_AND_into_one_pushed_down_predicate()
    {
        await SeedCarsAsync();
        var rowSecurity = Build([new SoftDeletePolicy()]);
        using var session = Store.OpenAsyncSession();

        var composition = await rowSecurity.ComposeRowFilterAsync(session.Query<RpCar>(), typeof(RpCar), typeof(RpCar), "Query");
        var query = (IRavenQueryable<RpCar>)composition.Queryable;

        composition.Mode.Should().Be(RowFilterMode.PushedDown);
        query.ToString().Should().Contain("where (LicensePlate != $p0 and IsDeleted != $p1)");
        (await query.Select(c => c.Id).ToListAsync()).OrderBy(i => i).Should().Equal("RpCars/1", "RpCars/4");
    }

    public class RpCars_Overview : Raven.Client.Documents.Indexes.AbstractIndexCreationTask<RpCar>
    {
        public RpCars_Overview()
        {
            Map = cars => from c in cars select new { c.LicensePlate, c.IsDeleted };
            StoreAllFields(Raven.Client.Documents.Indexes.FieldStorage.Yes);
        }
    }

    public class VRpCar
    {
        public string? Id { get; set; }
        public string LicensePlate { get; set; } = string.Empty;
        public bool IsDeleted { get; set; }
    }

    public class VRpCarWithoutPlate
    {
        public string? Id { get; set; }
        public bool IsDeleted { get; set; }
    }

    [Fact]
    public async Task Issue285_the_composed_filter_pushes_down_onto_an_index_projection_that_carries_its_members()
    {
        await SeedCarsAsync();
        await new RpCars_Overview().ExecuteAsync(Store);
        WaitForIndexing(Store);
        var rowSecurity = Build([new SoftDeletePolicy()]);
        using var session = Store.OpenAsyncSession();

        var projected = session.Query<VRpCar, RpCars_Overview>().ProjectInto<VRpCar>();
        var composition = await rowSecurity.ComposeRowFilterAsync(projected, typeof(RpCar), typeof(VRpCar), "Query");

        composition.Mode.Should().Be(RowFilterMode.PushedDownOntoProjection);
        var rows = await ((IRavenQueryable<VRpCar>)composition.Queryable).ToListAsync();
        rows.Select(r => r.Id).OrderBy(i => i).Should().Equal("RpCars/1", "RpCars/4");

        var partial = session.Query<VRpCarWithoutPlate, RpCars_Overview>().ProjectInto<VRpCarWithoutPlate>();
        (await rowSecurity.ComposeRowFilterAsync(partial, typeof(RpCar), typeof(VRpCarWithoutPlate), "Query"))
            .Mode.Should().Be(RowFilterMode.ProjectionFallback, "LicensePlate is not on the projection: the post-filter stays the gate");
    }

    [Fact]
    public async Task Single_row_checks_compile_the_same_composed_filter_plus_check_policies()
    {
        var rowSecurity = Build([new SoftDeletePolicy(), new LockPolicy()]);

        (await rowSecurity.IsAllowedAsync(typeof(RpCar), "Read", new RpCar { LicensePlate = "A" })).Should().BeTrue();
        (await rowSecurity.IsAllowedAsync(typeof(RpCar), "Read", new RpCar { LicensePlate = "A", IsDeleted = true })).Should().BeFalse();
        (await rowSecurity.IsAllowedAsync(typeof(RpCar), "Read", new RpCar { LicensePlate = "HIDDEN" })).Should().BeFalse();
        (await rowSecurity.IsAllowedAsync(typeof(RpCar), "Read", new RpCar { LicensePlate = "LOCKED" })).Should().BeTrue();
        (await rowSecurity.IsAllowedAsync(typeof(RpCar), "Edit", new RpCar { LicensePlate = "LOCKED" })).Should().BeFalse(
            "a check policy refines per action");
    }

    [Fact]
    public async Task A_check_policy_marks_the_composition_as_refined_so_paging_stays_in_memory()
    {
        var rowSecurity = Build([new SoftDeletePolicy(), new LockPolicy()]);
        using var session = Store.OpenAsyncSession();

        var composition = await rowSecurity.ComposeRowFilterAsync(session.Query<RpCar>(), typeof(RpCar), typeof(RpCar), "Query");

        composition.HasPerRowRefinement.Should().BeTrue();
        composition.CanPageInDatabase.Should().BeFalse();
    }

    [Fact]
    public async Task Constant_false_from_any_policy_short_circuits_and_constant_true_is_dropped()
    {
        using var session = Store.OpenAsyncSession();

        TenantPolicy.DenyAll = false;
        var permissive = Build([new TenantPolicy()]);
        (await permissive.ComposeRowFilterAsync(session.Query<RpTag>(), typeof(RpTag), typeof(RpTag), "Query"))
            .Mode.Should().Be(RowFilterMode.NoRule, "x => true restricts nothing and is dropped");

        TenantPolicy.DenyAll = true;
        try
        {
            var denying = Build([new SoftDeletePolicy(), new TenantPolicy()]);
            var filter = await denying.GetFilterExpressionAsync(typeof(RpTag), "Query");
            filter!.Body.Should().BeOfType<ConstantExpression>();
            ((ConstantExpression)filter.Body).Value.Should().Be(false);
            (await denying.ComposeRowFilterAsync(session.Query<RpTag>(), typeof(RpTag), typeof(RpTag), "Query"))
                .Mode.Should().Be(RowFilterMode.ConstantPredicate);
        }
        finally
        {
            TenantPolicy.DenyAll = false;
        }
    }

    [Fact]
    public async Task The_system_context_keeps_only_policies_that_opt_out_of_the_exemption()
    {
        var soft = new SoftDeletePolicy();
        var rowSecurity = Build([soft, new LockPolicy()], SystemContext());

        (await rowSecurity.IsAllowedAsync(typeof(RpCar), "Read", new RpCar { LicensePlate = "HIDDEN" })).Should().BeTrue(
            "the actions filter scopes viewers, not the system");
        (await rowSecurity.IsAllowedAsync(typeof(RpCar), "Edit", new RpCar { LicensePlate = "LOCKED" })).Should().BeTrue(
            "the lock policy keeps the default exemption");
        (await rowSecurity.IsAllowedAsync(typeof(RpCar), "Read", new RpCar { LicensePlate = "A", IsDeleted = true })).Should().BeFalse(
            "soft deletion opted out of the exemption, so background work does not see deleted rows");
        soft.LastContext!.IsSystemContext.Should().BeTrue();

        var plain = Build([], SystemContext());
        using var session = Store.OpenAsyncSession();
        (await plain.ComposeRowFilterAsync(session.Query<RpCar>(), typeof(RpCar), typeof(RpCar), "Query"))
            .Mode.Should().Be(RowFilterMode.SystemContext, "no participating policy: exactly today's exemption");
    }

    [Fact]
    public async Task Policies_run_once_per_type_and_action_per_request_and_see_the_request_flags()
    {
        var soft = new SoftDeletePolicy();
        var rowSecurity = Build([soft], deleted: SparkDeletedFilter.Include);

        for (var i = 0; i < 25; i++)
            await rowSecurity.IsAllowedAsync(typeof(RpCar), "Read", new RpCar { LicensePlate = "A", IsDeleted = true });
        await rowSecurity.IsAllowedAsync(typeof(RpTag), "Read", new RpTag());

        soft.Calls[(typeof(RpCar), "Read")].Should().Be(1);
        soft.Calls[(typeof(RpTag), "Read")].Should().Be(1);
        soft.LastContext!.Deleted.Should().Be(SparkDeletedFilter.Include);
        (await rowSecurity.IsAllowedAsync(typeof(RpCar), "Read", new RpCar { LicensePlate = "A", IsDeleted = true }))
            .Should().BeTrue("the policy honoured the request's deleted=include");
    }

    [Fact]
    public void S5_row_rule_kinds_tell_visibility_decisions_from_other_filters()
    {
        var rowSecurity = Build([new SoftDeletePolicy(), new TenantPolicy(), new LockPolicy()]);

        var plain = rowSecurity.GetRowRuleKinds(typeof(RpPlain));
        plain.Should().Be(RowRuleKinds.None);
        rowSecurity.HasRowRule(typeof(RpPlain)).Should().BeFalse();

        var softOnly = Build([new SoftDeletePolicy()]).GetRowRuleKinds(typeof(RpTag));
        softOnly.Should().Be(RowRuleKinds.FilterPolicy);
        softOnly.DecidesVisibility().Should().BeFalse("soft deletion must not satisfy the anonymous-readable validator");
        softOnly.RefusesAuthorPagedTotals().Should().BeFalse("a filter-only, non-visibility policy does not refuse SparkQueryPage");

        var tag = rowSecurity.GetRowRuleKinds(typeof(RpTag));
        tag.Should().Be(RowRuleKinds.FilterPolicy | RowRuleKinds.VisibilityPolicy);
        tag.DecidesVisibility().Should().BeTrue();
        tag.RefusesAuthorPagedTotals().Should().BeTrue();

        var car = rowSecurity.GetRowRuleKinds(typeof(RpCar));
        car.Should().Be(RowRuleKinds.ActionsRule | RowRuleKinds.FilterPolicy | RowRuleKinds.CheckPolicy);
        car.RefusesAuthorPagedTotals().Should().BeTrue();
    }

    [Fact]
    public void S5_the_declaration_validator_still_reports_an_anonymous_type_whose_only_rule_is_soft_deletion()
    {
        var rowSecurity = Build([new SoftDeletePolicy()]);
        var anonymous = Guid.NewGuid();
        var tagType = new EntityTypeDefinition { Id = Guid.NewGuid(), Name = nameof(RpTag), ClrType = typeof(RpTag).FullName! };
        var configuration = new SecurityConfiguration
        {
            WellKnown = new() { [SparkWellKnownGroups.Anonymous] = anonymous.ToString() },
            Rights = [new Right { Id = Guid.NewGuid(), GroupId = anonymous, Resource = $"Query/{nameof(RpTag)}" }],
        };

        var problems = RowPolicyDeclarationValidator.Validate(
            configuration, [tagType],
            _ => rowSecurity.GetRowRuleKinds(typeof(RpTag)).DecidesVisibility(),
            _ => null);

        problems.Should().ContainSingle();
    }
}
