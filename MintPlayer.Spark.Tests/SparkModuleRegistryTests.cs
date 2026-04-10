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

        Assert.Equal(2, callCount);
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

        Assert.Equal(3, callCount);
    }

    [Fact]
    public void ApplyMiddleware_WithNoActions_DoesNotThrow()
    {
        var registry = new SparkModuleRegistry();
        var app = Substitute.For<IApplicationBuilder>();

        registry.ApplyMiddleware(app);
    }

    [Fact]
    public void MapEndpoints_WithNoActions_DoesNotThrow()
    {
        var registry = new SparkModuleRegistry();
        var endpoints = Substitute.For<IEndpointRouteBuilder>();

        registry.MapEndpoints(endpoints);
    }

    [Fact]
    public void ApplyMiddleware_PassesAppToActions()
    {
        var registry = new SparkModuleRegistry();
        var app = Substitute.For<IApplicationBuilder>();
        IApplicationBuilder? captured = null;

        registry.AddMiddleware(a => captured = a);
        registry.ApplyMiddleware(app);

        Assert.Same(app, captured);
    }

    [Fact]
    public void MapEndpoints_PassesEndpointsToActions()
    {
        var registry = new SparkModuleRegistry();
        var endpoints = Substitute.For<IEndpointRouteBuilder>();
        IEndpointRouteBuilder? captured = null;

        registry.AddEndpoints(e => captured = e);
        registry.MapEndpoints(endpoints);

        Assert.Same(endpoints, captured);
    }

    [Fact]
    public void IdentityUserType_DefaultsToNull()
    {
        var registry = new SparkModuleRegistry();

        Assert.Null(registry.IdentityUserType);
    }

    [Fact]
    public void IdentityUserType_CanBeSet()
    {
        var registry = new SparkModuleRegistry();

        registry.IdentityUserType = typeof(string);

        Assert.Equal(typeof(string), registry.IdentityUserType);
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

        Assert.Equal([1, 2, 3], order);
    }
}
