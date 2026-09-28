using System.Linq.Expressions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Configuration;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Services.Breadcrumb;
using MintPlayer.Spark.Testing;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// #283 regression (breadcrumbs must not disclose a row the caller may not read) with row policies in
/// play, and spike S8 of #460: breadcrumbs across three levels with three policies stay under the
/// session's 30-request cap and call each filter policy at most once per (type, action).
/// </summary>
/// <remarks>
/// The #283 fix itself is in <c>BreadcrumbResolver</c> (every referenced document passes the row
/// Read check before its label is rendered). What this adds is that the check it asks is the composed
/// one: a row hidden by a <em>policy</em>, not by its Actions class, is redacted just the same.
/// </remarks>
public class RowPolicyBreadcrumbTests : SparkTestDriver
{
    public interface IBpSoftDeletable
    {
        bool IsDeleted { get; set; }
    }

    public class BpPerson : IBpSoftDeletable
    {
        public string? Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool IsDeleted { get; set; }
    }

    public class BpCar : IBpSoftDeletable
    {
        public string? Id { get; set; }
        public string Plate { get; set; } = string.Empty;
        public string? Driver { get; set; }
        public bool IsDeleted { get; set; }
    }

    public class BpSpot
    {
        public string? Id { get; set; }
        public string Coordinates { get; set; } = string.Empty;
        public string? ParkedCar { get; set; }
    }

    /// <summary>Counts every GetFilterAsync call, per (type, action), across all three policies.</summary>
    public sealed class CallCounter
    {
        public Dictionary<(string Policy, Type Type, string Action), int> Calls { get; } = [];

        public void Add(string policy, RowPolicyContext context)
        {
            var key = (policy, context.EntityType, context.Action);
            Calls[key] = Calls.GetValueOrDefault(key) + 1;
        }
    }

    public sealed class SoftDeletePolicy(CallCounter counter) : RowFilterPolicy<IBpSoftDeletable>
    {
        public override ValueTask<Expression<Func<IBpSoftDeletable, bool>>?> GetFilterAsync(RowPolicyContext context)
        {
            counter.Add(nameof(SoftDeletePolicy), context);
            return ValueTask.FromResult<Expression<Func<IBpSoftDeletable, bool>>?>(x => x.IsDeleted != true);
        }
    }

    /// <summary>Applies to every type and restricts nothing — present so the budget is measured with three policies.</summary>
    public sealed class EveryTypePolicy(CallCounter counter) : IRowFilterPolicy
    {
        public bool AppliesTo(Type entityType) => true;
        public bool IsVisibilityDecision => true;

        public ValueTask<LambdaExpression?> GetFilterAsync(RowPolicyContext context)
        {
            counter.Add(nameof(EveryTypePolicy), context);
            return ValueTask.FromResult<LambdaExpression?>(null);
        }
    }

    public sealed class BlockedCarPolicy : RowCheckPolicy<BpCar>
    {
        public override ValueTask<bool> IsAllowedAsync(RowPolicyContext context, BpCar entity)
            => ValueTask.FromResult(entity.Plate != "BLOCKED");
    }

    private static EntityAttributeDefinition Scalar(string name) =>
        new() { Id = Guid.NewGuid(), Name = name, DataType = "string" };
    private static EntityAttributeDefinition Ref(string name, Type target) =>
        new() { Id = Guid.NewGuid(), Name = name, DataType = "Reference", ReferenceType = target.FullName };
    private static EntityTypeDefinition Def(Type clr, string breadcrumb, params EntityAttributeDefinition[] attrs) =>
        new() { Id = Guid.NewGuid(), Name = clr.Name, ClrType = clr.FullName!, Breadcrumb = breadcrumb, Attributes = attrs };

    private static (IBreadcrumbResolver Resolver, EntityTypeDefinition Spot, CallCounter Counter) Build()
    {
        var person = Def(typeof(BpPerson), "{Name}", Scalar("Name"));
        var car = Def(typeof(BpCar), "{Plate} ({Driver})", Scalar("Plate"), Ref("Driver", typeof(BpPerson)));
        var spot = Def(typeof(BpSpot), "{ParkedCar} ({Coordinates})", Scalar("Coordinates"), Ref("ParkedCar", typeof(BpCar)));
        EntityTypeDefinition[] defs = [person, car, spot];

        var loader = Substitute.For<IModelLoader>();
        loader.GetEntityTypes().Returns(defs);
        var byClr = defs.ToDictionary(d => d.ClrType!, d => d, StringComparer.Ordinal);
        loader.GetEntityTypeByClrType(Arg.Any<string>()).Returns(ci => byClr.GetValueOrDefault((string)ci[0]!));

        var mapper = new EntityMapper(loader);
        var actions = Substitute.For<IActionsResolver>();
        actions.ResolveForType(typeof(BpPerson)).Returns(new DefaultPersistentObjectActions<BpPerson>(mapper));
        actions.ResolveForType(typeof(BpCar)).Returns(new DefaultPersistentObjectActions<BpCar>(mapper));
        actions.ResolveForType(typeof(BpSpot)).Returns(new DefaultPersistentObjectActions<BpSpot>(mapper));

        var counter = new CallCounter();
        var rowSecurity = new RowSecurity(actions, rowPolicies: [new SoftDeletePolicy(counter), new EveryTypePolicy(counter), new BlockedCarPolicy()]);
        var resolver = new BreadcrumbResolver(loader, new BreadcrumbClosure(loader), rowSecurity, new SparkOptions());
        return (resolver, spot, counter);
    }

    private async Task SeedAsync(int n)
    {
        using var seed = Store.OpenAsyncSession();
        for (var i = 0; i < n; i++)
        {
            await seed.StoreAsync(new BpPerson { Name = $"P{i}", IsDeleted = i == 1 }, $"people/{i}");
            await seed.StoreAsync(new BpCar { Plate = i == 2 ? "BLOCKED" : $"CAR-{i}", Driver = $"people/{i}", IsDeleted = i == 3 }, $"cars/{i}");
            await seed.StoreAsync(new BpSpot { Coordinates = $"{i},{i}", ParkedCar = $"cars/{i}" }, $"spots/{i}");
        }
        await seed.SaveChangesAsync();
    }

    [Fact]
    public async Task Issue283_a_reference_hidden_by_a_policy_renders_the_redacted_placeholder()
    {
        await SeedAsync(5);
        var (resolver, spot, _) = Build();
        using var session = Store.OpenAsyncSession();
        var spots = (await session.LoadAsync<BpSpot>(Enumerable.Range(0, 5).Select(i => $"spots/{i}"))).Values.Cast<object>().ToList();

        var result = await resolver.ResolveAsync(session, spots, spot);

        result.Get("spots/0").Should().Be("CAR-0 (P0) (0,0)", "nothing hides this chain");
        result.Get("cars/1").Should().Be("CAR-1 (—)", "the soft-deleted driver is hidden by a filter policy");
        result.Get("spots/2").Should().Be("— (2,2)", "the blocked car is hidden by a check policy");
        result.Get("spots/3").Should().Be("— (3,3)", "the soft-deleted car is hidden by a filter policy");
        result.Get("people/2").Should().NotBe("P2", "a hidden car's driver is never reached through it");
    }

    [Fact]
    public async Task S8_breadcrumbs_with_three_policies_stay_under_the_request_budget_and_call_each_policy_once_per_type_and_action()
    {
        const int n = 50;
        await SeedAsync(n);
        var (resolver, spot, counter) = Build();
        using var session = Store.OpenAsyncSession();
        var spots = (await session.LoadAsync<BpSpot>(Enumerable.Range(0, n).Select(i => $"spots/{i}"))).Values.Cast<object>().ToList();

        var before = session.Advanced.NumberOfRequests;
        await resolver.ResolveAsync(session, spots, spot);
        var added = session.Advanced.NumberOfRequests - before;

        added.Should().Be(2, "one batched load per breadcrumb level (cars, then people) — policies add no request");
        session.Advanced.NumberOfRequests.Should().BeLessThan(30);
        counter.Calls.Values.Should().OnlyContain(count => count == 1, "the (type, action) memo covers policies too");
        counter.Calls.Keys.Select(k => k.Action).Distinct().Should().Equal("Read");
    }
}
