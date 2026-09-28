using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Tests.Authorization;

/// <summary>
/// #460, D12: <c>AddGroupMembershipProvider</c> composes, <c>UseGroupMembershipProvider</c> keeps
/// replacing — through the real registrations <c>AddSpark</c> makes.
/// </summary>
public class GroupMembershipCompositionTests
{
    private sealed class Primary : IGroupMembershipProvider
    {
        public Task<IEnumerable<string>> GetCurrentUserGroupsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IEnumerable<string>>(["Primary"]);
    }

    private sealed class Earned : IGroupMembershipProvider, IGroupIdMembershipProvider
    {
        public static readonly Guid Id = Guid.Parse("0e0e0e0e-0000-0000-0000-000000000001");

        public Task<IEnumerable<string>> GetCurrentUserGroupsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IEnumerable<string>>(["Earned"]);

        public Task<IEnumerable<Guid>> GetCurrentUserGroupIdsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IEnumerable<Guid>>([Id]);
    }

    private static async Task<SparkRequestGroups> GroupsAsync(Action<MintPlayer.Spark.Abstractions.Builder.ISparkBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSpark(configure);

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SparkGroupMembership>().GetAsync();
    }

    [Fact]
    public async Task Use_replaces_the_primary_and_Add_merges_regardless_of_order()
    {
        var groups = await GroupsAsync(spark => spark
            .AddGroupMembershipProvider<Earned>()
            .UseGroupMembershipProvider<Primary>());

        groups.Names.Should().BeEquivalentTo(["Primary", "Earned"]);
        groups.Ids.Should().BeEquivalentTo([Earned.Id]);
    }

    [Fact]
    public void Adding_the_same_provider_twice_is_a_no_op()
    {
        var services = new ServiceCollection();
        services.AddSpark(spark => spark
            .AddGroupMembershipProvider<Earned>()
            .AddGroupMembershipProvider<Earned>());

        services.Count(d => d.ServiceType == typeof(IComposedGroupMembershipProvider)).Should().Be(1);
    }

    [Fact]
    public async Task Without_either_call_only_the_claims_provider_is_asked()
    {
        // No HttpContext, so the claims provider returns nothing — and nothing else was registered.
        var groups = await GroupsAsync(_ => { });

        groups.Names.Should().BeEmpty();
        groups.Ids.Should().BeEmpty();
    }
}
