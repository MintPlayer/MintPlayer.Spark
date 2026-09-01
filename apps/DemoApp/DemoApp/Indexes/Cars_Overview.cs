using DemoApp.Library.Entities;
using MintPlayer.Spark.Abstractions;
using Raven.Client.Documents.Indexes;

namespace DemoApp.Indexes;

/// <summary>
/// The Car list screen's index, and — since <c>Company_Cars</c> joined it over the same collection —
/// the one that shapes <c>Car.json</c>. Exactly one projection-bearing index per collection may do
/// that, and the synchronizer refuses to guess: two candidates with no <c>[DefaultIndex]</c> is a
/// build error, not a coin toss.
/// </summary>
[DefaultIndex]
public partial class Cars_Overview : AbstractIndexCreationTask<Car>
{
    public Cars_Overview()
    {
        Map = cars => from car in cars
                      let owner = LoadDocument<Company>(car.Owner)
                      select new VCar
                      {
                          Id = car.Id,
                          LicensePlate = car.LicensePlate,
                          LicensePlateSort = car.LicensePlate,
                          Model = car.Model,
                          Year = car.Year,
                          OwnerFullName = owner != null ? owner.Name : null,
                          OwnerFullNameSort = owner != null ? owner.Name : null,
                          Status = car.Status
                      };

        // Applies the indexing declared by [Search]; generated from the attributes.
        IndexSearchFields();
        StoreAllFields(FieldStorage.Yes);
    }
}
