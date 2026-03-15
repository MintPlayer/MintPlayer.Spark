using Microsoft.AspNetCore.Routing;

namespace MintPlayer.AspNetCore.Endpoints;

/// <summary>
/// Contract for endpoint classes that are instantiated per-request via DI
/// using ActivatorUtilities.CreateInstance.
/// Implementing classes declare their route(s) via the static MapRoutes method.
/// </summary>
public interface IEndpoint
{
    /// <summary>
    /// Maps this endpoint's routes onto the given route builder.
    /// Called once at startup. Implementations should wire up handlers
    /// that resolve the endpoint instance per-request.
    /// </summary>
    static abstract void MapRoutes(IEndpointRouteBuilder routes);
}
