using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace MintPlayer.AspNetCore.Endpoints;

public static class EndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps all IEndpoint implementations from the given types onto a route group.
    /// Each endpoint's static MapRoutes method is invoked with the group.
    /// </summary>
    public static RouteGroupBuilder MapEndpoints(
        this IEndpointRouteBuilder routes,
        string groupPrefix,
        params Type[] endpointTypes)
    {
        var group = routes.MapGroup(groupPrefix);
        foreach (var type in endpointTypes)
        {
            var method = type.GetMethod(
                nameof(IEndpoint.MapRoutes),
                BindingFlags.Public | BindingFlags.Static,
                [typeof(IEndpointRouteBuilder)]);
            method?.Invoke(null, [group]);
        }
        return group;
    }

    /// <summary>
    /// Maps all IEndpoint implementations found in the given assemblies onto a route group.
    /// Discovers types implementing IEndpoint and calls MapRoutes on each.
    /// </summary>
    public static RouteGroupBuilder MapEndpoints(
        this IEndpointRouteBuilder routes,
        string groupPrefix,
        params Assembly[] assemblies)
    {
        var group = routes.MapGroup(groupPrefix);
        var endpointTypes = assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => !t.IsAbstract && !t.IsInterface && typeof(IEndpoint).IsAssignableFrom(t));

        foreach (var type in endpointTypes)
        {
            var method = type.GetMethod(
                nameof(IEndpoint.MapRoutes),
                BindingFlags.Public | BindingFlags.Static,
                [typeof(IEndpointRouteBuilder)]);
            method?.Invoke(null, [group]);
        }
        return group;
    }

    /// <summary>
    /// Creates a scoped endpoint instance using ActivatorUtilities.CreateInstance.
    /// Use this in MapRoutes implementations for per-request instantiation,
    /// especially for internal endpoint classes that can't use ASP.NET DI parameter binding.
    /// </summary>
    public static TEndpoint CreateEndpoint<TEndpoint>(this HttpContext context)
        where TEndpoint : class, IEndpoint
    {
        return ActivatorUtilities.CreateInstance<TEndpoint>(context.RequestServices);
    }
}
