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
        // Coalesce on ids: a selected row is a QueryResultItem, the parent is a PersistentObject,
        // and the two deliberately no longer unify -- a row is not a document.
        var carId = args.Parent?.Id ?? args.SelectedItems.FirstOrDefault()?.Id
            ?? throw new InvalidOperationException("No item selected");

        var car = await dbAccess.GetDocumentUncheckedAsync<Car>(carId);
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

        await dbAccess.SaveDocumentUncheckedAsync(copy);
    }
}
