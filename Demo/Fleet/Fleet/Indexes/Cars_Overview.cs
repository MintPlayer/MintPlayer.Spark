using Fleet.Entities;
using MintPlayer.Spark.Abstractions;
using Raven.Client.Documents.Indexes;

namespace Fleet.Indexes;

public class Cars_Overview : AbstractIndexCreationTask<Car>
{
    public Cars_Overview()
    {
        Map = cars => from car in cars
                      select new VCar
                      {
                          Id = car.Id,
                          LicensePlate = car.LicensePlate,
                          Model = car.Model,
                          Year = car.Year,
                          Color = car.Color,
                          Status = car.Status,
                          Brand = car.Brand,
                      };

        StoreAllFields(FieldStorage.Yes);
    }
}

[FromIndex(typeof(Cars_Overview))]
public class VCar
{
    public string? Id { get; set; }
    public string LicensePlate { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public int Year { get; set; }
    public string? Color { get; set; }
    public string? Status { get; set; }
    public string? Brand { get; set; }
}
