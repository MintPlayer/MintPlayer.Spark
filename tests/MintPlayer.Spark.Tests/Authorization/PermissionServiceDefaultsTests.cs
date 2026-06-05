using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Authorization;

/// <summary>
/// R2-H1 — AddSpark MUST register a fail-closed default IAccessControl. The
/// previous behavior let PermissionService no-op when IAccessControl wasn't
/// registered, which silently opened every endpoint on hosts that called
/// AddSpark without AddAuthorization. The fix:
///  - default registration: DenyAllAccessControl (deny everything)
///  - opt-in spark.AddAuthorization() → real AccessControlService
///  - opt-in spark.AllowAnonymousAccess() → AllowAllAccessControl
/// </summary>
public class PermissionServiceDefaultsTests : SparkTestDriver
{
    private static ServiceCollection NewServices(IDocumentStore store)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(store);
        return services;
    }

    [Fact]
    public async Task AddSpark_alone_denies_every_permission_check()
    {
        var services = NewServices(Store);
        services.AddSpark(spark => spark.UseContext<TestSparkContext>());

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var permissions = scope.ServiceProvider.GetRequiredService<IPermissionService>();

        (await permissions.IsAllowedAsync("Read", "Company")).Should().BeFalse(
            "fail-closed default IAccessControl returns false for every check");
        (await permissions.IsAllowedAsync("New", "Person")).Should().BeFalse();
        (await permissions.IsAllowedAsync("Delete", "Anything")).Should().BeFalse();
    }

    [Fact]
    public async Task AddSpark_alone_throws_on_EnsureAuthorized()
    {
        var services = NewServices(Store);
        services.AddSpark(spark => spark.UseContext<TestSparkContext>());

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var permissions = scope.ServiceProvider.GetRequiredService<IPermissionService>();

        var act = () => permissions.EnsureAuthorizedAsync("Read", "Company");
        await act.Should().ThrowAsync<SparkAccessDeniedException>(
            "EnsureAuthorized must throw when no policy allows the check");
    }

    [Fact]
    public async Task AllowAnonymousAccess_opts_into_permissive_IAccessControl()
    {
        var services = NewServices(Store);
        services.AddSpark(spark =>
        {
            spark.UseContext<TestSparkContext>();
            spark.AllowAnonymousAccess();
        });

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var permissions = scope.ServiceProvider.GetRequiredService<IPermissionService>();

        (await permissions.IsAllowedAsync("Read", "Company")).Should().BeTrue(
            "AllowAnonymousAccess installs an AllowAll IAccessControl");
        (await permissions.IsAllowedAsync("Delete", "Anything")).Should().BeTrue();
    }

    [Fact]
    public void AddAuthorization_overrides_the_default_deny_all()
    {
        var services = NewServices(Store);
        services.AddSpark(spark =>
        {
            spark.UseContext<TestSparkContext>();
            spark.AddAuthorization();
        });

        // Inspect descriptors rather than instantiating — AccessControlService
        // pulls IHostEnvironment etc. that we'd otherwise have to fake out.
        // The contract is: DI's last-wins resolution returns AccessControlService,
        // not DenyAllAccessControl.
        var accessControlRegistrations = services
            .Where(d => d.ServiceType == typeof(IAccessControl))
            .ToList();

        accessControlRegistrations.Should().HaveCountGreaterThan(1,
            "the deny-all default and the AddAuthorization registration both appear");

        var winner = accessControlRegistrations[^1];
        var winnerType = winner.ImplementationType
            ?? winner.ImplementationInstance?.GetType()
            ?? winner.ImplementationFactory?.Method.ReturnType;
        winnerType?.Name.Should().NotBe("DenyAllAccessControl",
            "AddAuthorization() must register an IAccessControl after the default deny-all");
        winnerType?.Name.Should().NotBe("AllowAllAccessControl");
    }

    private sealed class TestSparkContext : SparkContext { }
}
