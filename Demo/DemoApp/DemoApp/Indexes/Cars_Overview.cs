using DemoApp.Library.Entities;
using Raven.Client.Documents.Indexes;

namespace DemoApp.Indexes;

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
