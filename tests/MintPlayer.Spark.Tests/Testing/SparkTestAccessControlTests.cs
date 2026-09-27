using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Testing;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Testing;

/// <summary>
/// <see cref="SparkTestAccessControl"/> is public API of the shipped testing package, and nothing in
/// this repository used it — so nothing would have noticed if it stopped recording, or granted
/// case-sensitively, or left the real <see cref="IAccessControl"/> registered alongside itself.
/// </summary>
public class SparkTestAccessControlTests
{
    [Fact]
    public async Task AllowAll_and_DenyAll_decide_everything_and_record_every_question_in_order()
    {
        var allow = SparkTestAccessControl.AllowAll();
        var deny = SparkTestAccessControl.DenyAll();

        (await allow.IsAllowedAsync("Read/Car")).Should().BeTrue();
        (await allow.IsAllowedAsync("Edit/Car")).Should().BeTrue();
        (await allow.IsAllowedAsync("Read/Car")).Should().BeTrue();
        (await deny.IsAllowedAsync("Read/Car")).Should().BeFalse();

        // Repeats are kept: "asked twice" is itself a finding.
        allow.Asked.Should().Equal("Read/Car", "Edit/Car", "Read/Car");
        deny.Asked.Should().Equal("Read/Car");
    }

    [Fact]
    public async Task Granting_allows_exactly_the_listed_resources_case_insensitively()
    {
        var access = SparkTestAccessControl.Granting("Read/Car", "Edit/Car");

        (await access.IsAllowedAsync("read/car")).Should().BeTrue();
        (await access.IsAllowedAsync("Edit/Car")).Should().BeTrue();
        (await access.IsAllowedAsync("Delete/Car")).Should().BeFalse();
        // No bundle expansion: a granted action does not imply a broader one.
        (await access.IsAllowedAsync("ReadEdit/Car")).Should().BeFalse();
    }

    [Fact]
    public async Task Matching_decides_by_the_predicate()
    {
        var access = SparkTestAccessControl.Matching(resource => resource.StartsWith("Read/", StringComparison.Ordinal));

        (await access.IsAllowedAsync("Read/Person")).Should().BeTrue();
        (await access.IsAllowedAsync("Delete/Person")).Should().BeFalse();
        access.Asked.Should().HaveCount(2);
    }

    [Fact]
    public async Task Questions_asked_concurrently_are_all_recorded()
    {
        var access = SparkTestAccessControl.AllowAll();

        await Task.WhenAll(Enumerable.Range(0, 200).Select(i => Task.Run(() => access.IsAllowedAsync($"Read/Item{i}"))));

        access.Asked.Should().HaveCount(200);
    }

    [Fact]
    public void UseSparkTestAccessControl_replaces_the_registration_rather_than_adding_to_it()
    {
        var access = SparkTestAccessControl.DenyAll();
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IAccessControl>());
        services.AddScoped(_ => Substitute.For<IAccessControl>());

        services.UseSparkTestAccessControl(access).Should().BeSameAs(services);

        services.Where(d => d.ServiceType == typeof(IAccessControl)).Should().ContainSingle();
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IAccessControl>().Should().BeSameAs(access,
            "registered as the instance, so the test keeps the reference it asserts on");
    }
}
