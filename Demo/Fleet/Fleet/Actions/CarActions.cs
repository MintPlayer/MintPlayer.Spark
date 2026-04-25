using System.Security.Claims;
using Fleet.Entities;
using Fleet.Indexes;
using Fleet.LookupReferences;
using Microsoft.AspNetCore.Http;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Queries;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

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

    public override async Task OnDeleteAsync(IAsyncDocumentSession session, string id)
    {
        var entity = await session.LoadAsync<Car>(id);
        if (entity is null) return;

        // Virtual PO confirmation form — user must retype the plate. The Virtual PO is
        // scaffolded from Demo/Fleet/Fleet/App_Data/Model/ConfirmDeleteCar.json; the
        // populated values come back through manager.Retry.Result.PersistentObject.
        var popup = manager.GetPersistentObject(Guid.Parse(PersistentObjectIds.Default.ConfirmDeleteCar));
        popup["LicensePlate"].Value = entity.LicensePlate;

        manager.Retry.Action(
            title: "Delete car",
            options: ["Delete", "Cancel"],
            persistentObject: popup,
            message: $"Type the license plate to confirm deletion of {entity.LicensePlate}."
        );

        var result = manager.Retry.Result!;
        if (result.Option == "Cancel")
            return; // silent no-op — endpoint returns NoContent without actually deleting

        var typed = result.PersistentObject?["Confirmation"].Value?.ToString();
        if (!string.Equals(typed, entity.LicensePlate, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Confirmation '{typed}' does not match license plate '{entity.LicensePlate}'.");

        await OnBeforeDeleteAsync(entity);
        session.Delete(entity);
        await session.SaveChangesAsync();

        // Demo toast — surfaces a frontend notification after the retry-confirmation flow
        // completes so the user sees explicit feedback that the deletion went through.
        manager.Client.Notify($"Car {entity.LicensePlate} deleted", NotificationKind.Success);
    }

    /// <summary>
    /// Demo: emit a toast on the frontend after every successful save (Create + Update).
    /// </summary>
    public override Task OnAfterSaveAsync(PersistentObject obj, Car entity)
    {
        manager.Client.Notify($"Car {entity.LicensePlate} saved", NotificationKind.Success);
        return Task.CompletedTask;
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
