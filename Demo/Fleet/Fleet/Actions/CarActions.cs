using System.Security.Claims;
using Fleet.Entities;
using Fleet.Indexes;
using Fleet.LookupReferences;
using Microsoft.AspNetCore.Http;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Queries;
using Raven.Client.Documents.Linq;

namespace Fleet.Actions;

public partial class CarActions : DefaultPersistentObjectActions<Car>
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    private const string AdminRole = "Administrators";

    private ClaimsPrincipal? CurrentUser => httpContextAccessor.HttpContext?.User;
    private string? CurrentUserId => CurrentUser?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    private bool CurrentUserIsAdmin => CurrentUser?.IsInRole(AdminRole) == true;

    /// <summary>
    /// Row-level auth (H-2): administrators see/edit/delete everything; other authenticated
    /// users only act on cars they created. An unauthenticated caller (which would already be
    /// blocked by entity-type authz in Fleet's security.json) falls through to deny.
    /// </summary>
    public override Task<bool> IsAllowedAsync(string action, Car entity)
    {
        if (CurrentUserIsAdmin) return Task.FromResult(true);
        var userId = CurrentUserId;
        if (string.IsNullOrEmpty(userId)) return Task.FromResult(false);
        return Task.FromResult(string.Equals(entity.CreatedBy, userId, StringComparison.Ordinal));
    }

    public override async Task OnBeforeSaveAsync(PersistentObject obj, Car entity)
    {
        // Stamp the creator id on first save. Preserve it on subsequent updates so the
        // row-level auth check stays consistent even if the owner changes password/email.
        if (string.IsNullOrEmpty(entity.CreatedBy))
            entity.CreatedBy = CurrentUserId;

        var statusAttr = obj.Attributes.FirstOrDefault(a => a.Name == nameof(Car.Status));
        if (statusAttr?.IsValueChanged == true && entity.Status == CarStatus.Stolen)
        {
            // Step 0: Confirm marking as stolen
            manager.Retry.Action(
                title: "Report vehicle as stolen",
                options: ["Confirm"],
                message: $"Are you sure you want to mark {entity.LicensePlate} as stolen? This will lock the vehicle record."
            );

            if (manager.Retry.Result!.Option == "Cancel")
                return;

            // Step 1: Ask whether to notify fleet managers
            manager.Retry.Action(
                title: "Notify fleet managers",
                options: ["Yes, notify", "No, skip"],
                message: "Should all fleet managers be notified about this stolen vehicle?"
            );

            if (manager.Retry.Result!.Option == "Cancel")
                return;
        }

        await base.OnBeforeSaveAsync(obj, entity);
    }

    public override async Task OnBeforeDeleteAsync(Car entity)
    {
        manager.Retry.Action(
            title: "Confirm deletion",
            options: ["Delete"],
            message: $"Are you sure you want to delete {entity.LicensePlate}?"
        );

        if (manager.Retry.Result!.Option == "Cancel")
            return;

        await base.OnBeforeDeleteAsync(entity);
    }

    /// <summary>
    /// Custom query: returns cars reported as stolen.
    /// Source: "Custom.Stolen_Cars"
    /// </summary>
    public IRavenQueryable<VCar> Stolen_Cars(CustomQueryArgs args)
    {
        return args.Session.Query<VCar, Cars_Overview>()
            .Where(c => c.Status == CarStatus.Stolen);
    }
}
