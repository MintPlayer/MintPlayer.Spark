using DemoApp.Library.Entities;
using DemoApp.Library.LookupReferences;
using MintPlayer.Spark.Abstractions.Storage;

namespace DemoApp;

/// <summary>
/// The demo's starting data (#551 D14). DemoApp runs on the in-memory store, which is empty on every start, so this
/// is written once per process. Fictional records only.
/// </summary>
internal static class DemoSeed
{
    public static async Task WriteAsync(ISparkSession session, CancellationToken cancellationToken)
    {
        var companies = new[]
        {
            new Company { Id = "Companies/acme", Name = "Acme Logistics", Website = "https://acme.example", EmployeeCount = 120 },
            new Company { Id = "Companies/globex", Name = "Globex Software", Website = "https://globex.example", EmployeeCount = 45 },
            new Company { Id = "Companies/initech", Name = "Initech Consulting", EmployeeCount = 12 },
        };

        var bicycles = new[]
        {
            new Bicycle { Id = "Bicycles/roadster", Brand = "Velo", Model = "Roadster", FrameSize = 56 },
            new Bicycle { Id = "Bicycles/trail", Brand = "Ridge", Model = "Trail 29", FrameSize = 52 },
        };

        var motorcycles = new[]
        {
            new Motorcycle { Id = "Motorcycles/cruiser", Brand = "Thunder", Model = "Cruiser", EngineCapacity = 1200 },
            new Motorcycle { Id = "Motorcycles/scout", Brand = "Falcon", Model = "Scout", EngineCapacity = 650 },
        };

        var people = new[]
        {
            new Person
            {
                Id = "People/alex", FirstName = "Alex", LastName = "Example", Email = "alex@acme.example",
                DateOfBirth = new DateOnly(1988, 4, 12), Company = "Companies/acme", IsActive = true, Bicycle = "Bicycles/roadster",
                Address = new Address { Street = "1 Harbour Road", City = "Springfield", State = "North" },
            },
            new Person
            {
                Id = "People/sam", FirstName = "Sam", LastName = "Sample", Email = "sam@acme.example",
                DateOfBirth = new DateOnly(1992, 9, 3), Company = "Companies/acme", IsActive = true,
            },
            new Person
            {
                Id = "People/robin", FirstName = "Robin", LastName = "Placeholder", Email = "robin@globex.example",
                Company = "Companies/globex", IsActive = false, Bicycle = "Bicycles/trail",
            },
        };

        var cars = new[]
        {
            new Car { Id = "Cars/1", LicensePlate = "DEMO-001", Model = "Model A", Year = 2021, Color = "#1f77b4", Status = ECarStatus.InUse, Owner = "Companies/acme" },
            new Car { Id = "Cars/2", LicensePlate = "DEMO-002", Model = "Model B", Year = 2019, Color = "#d62728", Status = ECarStatus.OnParking, Owner = "Companies/acme" },
            new Car { Id = "Cars/3", LicensePlate = "DEMO-003", Model = "Model C", Year = 2023, Color = "#2ca02c", Status = ECarStatus.InMaintenance, Owner = "Companies/globex" },
        };

        var stocks = new[]
        {
            new Stock { Id = "Stocks/acme", Symbol = "ACME", CompanyName = "Acme Logistics", CurrentPrice = 42.10m, Change = 0.85m, ChangePercent = 2.06m },
            new Stock { Id = "Stocks/glbx", Symbol = "GLBX", CompanyName = "Globex Software", CurrentPrice = 118.40m, Change = -1.20m, ChangePercent = -1.00m },
        };

        foreach (var entity in companies.Concat<object>(bicycles).Concat(motorcycles).Concat(people).Concat(cars).Concat(stocks))
            await session.StoreAsync(entity, cancellationToken);
    }
}
