using Fleet.Entities;
using Fleet.LookupReferences;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;

namespace Fleet.Actions;

public partial class CarActions : DefaultPersistentObjectActions<Car>
{
    [Inject] private readonly IManager manager;

    public override async Task OnBeforeSaveAsync(PersistentObject obj, Car entity)
    {
        var statusAttr = obj.Attributes.FirstOrDefault(a => a.Name == nameof(Car.Status));
        if (statusAttr?.IsValueChanged == true && entity.Status == CarStatus.Stolen)
        {
            manager.Retry.Action(
                title: "Report vehicle as stolen",
                options: ["Confirm"],
                message: $"Are you sure you want to mark {entity.LicensePlate} as stolen? This will notify all fleet managers and lock the vehicle record."
            );

            // If we reach here, the user confirmed (Cancel is handled client-side)
        }

        await base.OnBeforeSaveAsync(obj, entity);
    }
}
