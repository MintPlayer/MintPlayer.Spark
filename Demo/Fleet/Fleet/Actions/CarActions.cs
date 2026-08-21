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
    [Inject] private readonly IAsyncDocumentSession session;

    private const string AdminRole = "Administrators";

    private ClaimsPrincipal? CurrentUser => httpContextAccessor.HttpContext?.User;
    private string? CurrentUserId => CurrentUser?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    private bool CurrentUserIsAdmin => CurrentUser?.IsInRole(AdminRole) == true;

    /// <summary>
    /// Row-level auth (H-2), expression form (#236): administrators and service/machine principals
    /// see/edit/create everything (null = no restriction); regular *users* only act on cars they
    /// created; a truly anonymous caller (already blocked by entity-type authz in Fleet's
    /// security.json) gets a filter that matches nothing. The framework pushes this into the
    /// RavenDB query on list paths, compiles it for single-row (detail/edit/delete) checks, and —
    /// since #236 M2 — evaluates it as a WITH CHECK on create. That last point is why the
    /// service-account case matters: a machine token with <c>ReadEditNew/Car</c> has no user id to
    /// own a row by, so it must be treated as unrestricted rather than denied — otherwise it could
    /// not create the cars its type-level right grants. One rule, every path.
    /// </summary>
    public override Task<System.Linq.Expressions.Expression<Func<Car, bool>>?> GetRowFilterAsync(string action)
    {
        System.Linq.Expressions.Expression<Func<Car, bool>>? filter;
        if (CurrentUserIsAdmin) filter = null;
        else
        {
            var userId = CurrentUserId;
            if (!string.IsNullOrEmpty(userId)) filter = car => car.CreatedBy == userId;
            // Authenticated but no user id → a service/machine principal acting under type-level
            // rights (e.g. Machine:FleetApi). Not a person to scope rows to, so unrestricted.
            else if (CurrentUser?.Identity?.IsAuthenticated == true) filter = null;
            // Truly anonymous → nothing.
            else filter = car => false;
        }
        return Task.FromResult(filter);
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
    /// Default includes (#239): always RavenDB-<c>.Include()</c> the <c>Manager</c> reference when
    /// loading or listing cars, so its breadcrumb resolves in the same round-trip instead of a
    /// follow-up load. <c>Manager</c> is a <c>[Reference]</c>, so it is auto-included already — this
    /// shows the hook, and it is also where you would add references the attribute doesn't cover
    /// (e.g. a deeper embedded path, or a read-only reference you don't want as an editable field).
    /// </summary>
    public override IReadOnlyCollection<string>? GetDefaultIncludes()
        => [nameof(Car.Manager)];

    /// <summary>
    /// Custom query: returns cars reported as stolen.
    /// Source: "Custom.Stolen_Cars"
    /// </summary>
    public IRavenQueryable<VCar> Stolen_Cars()
    {
        return session.Query<VCar, Cars_Overview>()
            .Where(c => c.Status == CarStatus.Stolen);
    }

    /// <summary>
    /// Custom query: cars from 2020 onward. The point of this demo query is that <b>row security
    /// composes onto it for free</b> — a non-admin sees only their own recent cars with no code
    /// here, because the framework applies <c>GetRowFilterAsync</c> to every query surface. Since
    /// <c>VCar</c> (the <c>Cars_Overview</c> projection) doesn't carry <c>CreatedBy</c>, the filter
    /// can't push down here, so it's applied via the post-materialization fallback — still correct,
    /// just not pushed into RQL.
    /// <para>
    /// Deliberately <c>async</c>, and deliberately returning the queryable rather than a list: this
    /// is the shape that used to lose every capability the sync one keeps (#294). The query declares
    /// <c>sortColumns</c> on <c>Year</c> in Car.json, so if async queries were second-class again the
    /// grid would come back unsorted — visibly, on screen, with nothing failing in the test suite.
    /// </para>
    /// Source: "Custom.Recent_Cars"
    /// </summary>
    public async Task<IRavenQueryable<VCar>> Recent_Cars()
    {
        return await Task.FromResult(session.Query<VCar, Cars_Overview>()
            .Where(c => c.Year >= 2020));
    }
}
