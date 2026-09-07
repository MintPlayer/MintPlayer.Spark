using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.PersistentObject;

internal sealed partial class GetPersistentObject : IGetEndpoint, IMemberOf<PersistentObjectGroup>
{
    public static string Path => "/{objectTypeId}/{**id}";

    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly Abstractions.ClientOperations.IClientAccessor clientAccessor;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var objectTypeId = httpContext.Request.RouteValues["objectTypeId"]!.ToString()!;

        // The id is a catch-all segment, so it also matches the bare "/{objectTypeId}" path — with
        // nothing in it. That used to be someone else's route: a list endpoint sat on the bare path
        // and won the match. It was deleted (a second, uncapped list pipeline), and this route
        // inherited the shape, where the ! on a null RouteValue was a NullReferenceException and a
        // 500 rather than a refusal.
        //
        // Answer exactly as for an id that names nothing, which is what an empty id is.
        var id = httpContext.Request.RouteValues["id"]?.ToString();
        if (string.IsNullOrEmpty(id))
        {
            return SparkDenial.RefuseJson(httpContext);
        }

        var entityType = modelLoader.ResolveEntityType(objectTypeId);
        if (entityType is null)
        {
            return SparkDenial.RefuseJson(httpContext);
        }

        try
        {
            var decodedId = Uri.UnescapeDataString(id);
            var obj = await databaseAccess.GetPersistentObjectAsync(entityType.Id, decodedId);

            if (obj is null)
            {
                return SparkDenial.RefuseJson(httpContext);
            }

            // Withholds issued through IClientAccessor used to be dropped here, silently. They ride
            // the client-operation envelope, and this endpoint returns a bare JSON object — so an
            // author who called DisableActionsOn(po, ...) inside OnLoadAsync got no error and no
            // effect, while PersistentObject.DisableActions on the same object worked. Two APIs that
            // looked interchangeable, one of which did nothing on the one path most likely to use it.
            //
            // Folded onto the object instead of switching this endpoint to the envelope: the client
            // already reads disabledActions off the PO, so this needs no wire change, and the two
            // APIs converge on one field rather than one growing a second delivery mechanism.
            MergeClientWithholds(obj);

            return Results.Json(obj);
        }
        catch (SparkAccessDeniedException)
        {
            return SparkDenial.RefuseJson(httpContext);
        }
    }

    /// <summary>
    /// Folds every <see cref="IClientAccessor"/> withhold aimed at this object onto the object
    /// itself, so both APIs land in the same place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three target shapes reach a detail response. A PO target naming this object's type and id is
    /// obviously ours. A current-response target is ours because this endpoint IS the current
    /// response. A session target applies to everything the caller sees, so it applies here too.
    /// </para>
    /// <para>
    /// A query target is deliberately not folded in: it names a query's result view, which this is
    /// not, and quietly widening it to a detail page would disable an action somewhere its author
    /// never asked for.
    /// </para>
    /// <para>
    /// <b>This is an affordance, not a permission.</b> It removes a button; it does not refuse the
    /// action. The action's own right is what refuses it, and that is re-checked on execute.
    /// </para>
    /// </remarks>
    private void MergeClientWithholds(Abstractions.PersistentObject obj)
    {
        var names = clientAccessor.Operations
            .OfType<Abstractions.ClientOperations.DisableActionOperation>()
            .Where(op => op.Target switch
            {
                Abstractions.ClientOperations.PersistentObjectDisableTarget t
                    => t.ObjectTypeId == obj.ObjectTypeId
                       && string.Equals(t.Id, obj.Id, StringComparison.Ordinal),
                Abstractions.ClientOperations.CurrentResponseDisableTarget => true,
                Abstractions.ClientOperations.SessionDisableTarget => true,
                _ => false,
            })
            .Select(op => op.ActionName)
            .ToArray();

        if (names.Length > 0)
            obj.DisableActions(names);
    }
}
