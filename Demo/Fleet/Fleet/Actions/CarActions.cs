using Fleet.Entities;
using Fleet.LookupReferences;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Queries;
using Raven.Client.Documents.Linq;

namespace Fleet.Actions;

public partial class CarActions : DefaultPersistentObjectActions<Car>
{
    [Inject] private readonly IManager manager;

    public override async Task OnBeforeSaveAsync(PersistentObject obj, Car entity)
    {
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
    /// Custom query: returns cars filtered by status.
    /// Source: "Custom.Cars_ByStatus"
    /// </summary>
    public IRavenQueryable<Car> Cars_ByStatus(CustomQueryArgs args)
    {
        args.EnsureParent("Car");
        var status = args.Parent!.Attributes.FirstOrDefault(a => a.Name == nameof(Car.Status))?.Value as string;
        return args.Session.Query<Car>()
            .Where(c => c.Status == status);
    }
}
