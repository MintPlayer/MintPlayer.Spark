using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using MintPlayer.Spark.Abstractions.Builder;
using NSubstitute;

namespace MintPlayer.Spark.Tests;

public class SparkModuleRegistryTests
{
    [Fact]
    public void AddMiddleware_ApplyMiddleware_InvokesActions()
    {
        var registry = new SparkModuleRegistry();
        var app = Substitute.For<IApplicationBuilder>();
        var callCount = 0;

        registry.AddMiddleware(_ => callCount++);
        registry.AddMiddleware(_ => callCount++);
        registry.ApplyMiddleware(app);

        callCount.Should().Be(2);
    }

    [Fact]
    public void AddEndpoints_MapEndpoints_InvokesActions()
    {
        var registry = new SparkModuleRegistry();
        var endpoints = Substitute.For<IEndpointRouteBuilder>();
        var callCount = 0;

        registry.AddEndpoints(_ => callCount++);
        registry.AddEndpoints(_ => callCount++);
        registry.AddEndpoints(_ => callCount++);
        registry.MapEndpoints(endpoints);

        callCount.Should().Be(3);
    }

    [Fact]
    public void ApplyMiddleware_WithNoActions_DoesNotThrow()
    {
        var registry = new SparkModuleRegistry();
        var app = Substitute.For<IApplicationBuilder>();

        var act = () => registry.ApplyMiddleware(app);

        act.Should().NotThrow();
    }

    [Fact]
    public void MapEndpoints_WithNoActions_DoesNotThrow()
    {
        var registry = new SparkModuleRegistry();
        var endpoints = Substitute.For<IEndpointRouteBuilder>();

        var act = () => registry.MapEndpoints(endpoints);

        act.Should().NotThrow();
    }

    [Fact]
    public void ApplyMiddleware_PassesAppToActions()
    {
        var registry = new SparkModuleRegistry();
        var app = Substitute.For<IApplicationBuilder>();
        IApplicationBuilder? captured = null;

        registry.AddMiddleware(a => captured = a);
        registry.ApplyMiddleware(app);

        captured.Should().BeSameAs(app);
    }

    [Fact]
    public void MapEndpoints_PassesEndpointsToActions()
    {
        var registry = new SparkModuleRegistry();
        var endpoints = Substitute.For<IEndpointRouteBuilder>();
        IEndpointRouteBuilder? captured = null;

        registry.AddEndpoints(e => captured = e);
        registry.MapEndpoints(endpoints);

        captured.Should().BeSameAs(endpoints);
    }

    [Fact]
    public void IdentityUserType_DefaultsToNull()
    {
        var registry = new SparkModuleRegistry();

        registry.IdentityUserType.Should().BeNull();
    }

    [Fact]
    public void IdentityUserType_CanBeSet()
    {
        var registry = new SparkModuleRegistry();

        registry.IdentityUserType = typeof(string);

        registry.IdentityUserType.Should().Be(typeof(string));
    }

    [Fact]
    public void ApplyMiddleware_InvokesActionsInOrder()
    {
        var registry = new SparkModuleRegistry();
        var app = Substitute.For<IApplicationBuilder>();
        var order = new List<int>();

        registry.AddMiddleware(_ => order.Add(1));
        registry.AddMiddleware(_ => order.Add(2));
        registry.AddMiddleware(_ => order.Add(3));
        registry.ApplyMiddleware(app);

        order.Should().Equal(1, 2, 3);
    }
}
