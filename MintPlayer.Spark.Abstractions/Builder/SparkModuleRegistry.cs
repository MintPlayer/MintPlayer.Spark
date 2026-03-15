using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using MintPlayer.AspNetCore.Endpoints;

namespace MintPlayer.Spark.Abstractions.Builder;

public class SparkModuleRegistry
{
    public Type? IdentityUserType { get; set; }

    private readonly List<Action<IApplicationBuilder>> middlewareActions = [];
    private readonly List<Action<IEndpointRouteBuilder>> endpointActions = [];
    private readonly List<(string GroupPrefix, Type[] EndpointTypes)> endpointGroups = [];

    public void AddMiddleware(Action<IApplicationBuilder> action) => middlewareActions.Add(action);
    public void AddEndpoints(Action<IEndpointRouteBuilder> action) => endpointActions.Add(action);

    public void AddEndpointGroup(string groupPrefix, params Type[] endpointTypes)
        => endpointGroups.Add((groupPrefix, endpointTypes));

    public void ApplyMiddleware(IApplicationBuilder app)
    {
        foreach (var action in middlewareActions)
            action(app);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        foreach (var action in endpointActions)
            action(endpoints);

        foreach (var (prefix, types) in endpointGroups)
            endpoints.MapEndpoints(prefix, types);
    }
}
