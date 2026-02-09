using DemoApp.Data;
using DemoApp.Library.Entities;
using Raven.Client.Documents.Indexes;

namespace DemoApp.Indexes;

public class Cars_Overview : AbstractIndexCreationTask<Car>
{
    public Cars_Overview()
    {
        Map = cars => from car in cars
                      let owner = LoadDocument<Company>(car.Owner)
                      select new VCar
                      {
                          Id = car.Id,
                          LicensePlate = car.LicensePlate,
                          Model = car.Model,
                          Year = car.Year,
                          OwnerFullName = owner != null ? owner.Name : null,
                          Status = car.Status
                      };

        Index(nameof(VCar.LicensePlate), FieldIndexing.Search);
        Index(nameof(VCar.OwnerFullName), FieldIndexing.Search);
        StoreAllFields(FieldStorage.Yes);
    }
}
