using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.Assertions;
using QnA.Testing;

namespace QnA.Tests;

/// <summary>
/// Whether <c>/qna-test/moderation/*</c> is mapped is decided by <see cref="QnATestSeamsGroup"/>'s
/// <c>IsEnabled</c>, at map time, from the root provider's configuration and environment. Configured
/// on in Production it must refuse to start, as the old startup check in <c>Program</c> did.
/// (The mapped case, with antiforgery on every route, is in <c>RouteSnapshots/QnA.txt</c>.)
/// </summary>
public class TestSeamsGroupTests
{
    private static bool IsEnabled<TGroup>(IServiceProvider services) where TGroup : IEndpointGroup
        => TGroup.IsEnabled(services);

    private static IServiceProvider Root(string environment, string? enabled)
        => new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { [QnATestSeams.EnabledKey] = enabled })
                .Build())
            .AddSingleton<IHostEnvironment>(new HostingEnvironment { EnvironmentName = environment })
            .BuildServiceProvider();

    [Fact]
    public void On_outside_Production_maps_the_seams()
        => IsEnabled<QnATestSeamsGroup>(Root(Environments.Development, "true")).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public void Off_leaves_them_unmapped_even_in_Production(string? enabled)
        => IsEnabled<QnATestSeamsGroup>(Root(Environments.Production, enabled)).Should().BeFalse();

    [Fact]
    public void On_in_Production_refuses_to_start()
    {
        var map = () => IsEnabled<QnATestSeamsGroup>(Root(Environments.Production, "true"));

        map.Should().Throw<InvalidOperationException>().WithMessage($"*'{QnATestSeams.EnabledKey}' is set in Production*");
    }
}
