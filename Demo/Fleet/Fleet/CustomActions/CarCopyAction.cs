using Fleet.Entities;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Actions;

namespace Fleet.CustomActions;

public partial class CarCopyAction : SparkCustomAction
{
    [Inject] private readonly IDatabaseAccess dbAccess;

    public override async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken)
    {
        // Support both detail view (parent) and query view (selectedItems)
        var source = args.Parent ?? args.SelectedItems.FirstOrDefault();
        if (source is null)
            throw new InvalidOperationException("No item selected");

        var carId = source.Id
            ?? throw new InvalidOperationException("Selected item has no ID");

        var car = await dbAccess.GetDocumentAsync<Car>(carId);
        if (car == null)
            throw new InvalidOperationException("Car not found");

        var copy = new Car
        {
            LicensePlate = $"{car.LicensePlate} (copy)",
            Model = car.Model,
            Year = car.Year,
            Color = car.Color,
            Brand = car.Brand,
            Status = car.Status,
        };

        await dbAccess.SaveDocumentAsync(copy);
    }
}
