using System.Drawing;

namespace Fleet.Contracts;

/// <summary>
/// Version 1 of the <c>cars</c> replication contract Fleet publishes: the fields of a car other
/// modules may read. Fleet generates its ETL transform from the projection in <c>Program.cs</c>;
/// HR's replica type (<c>HR.Replicated.Car</c>) has this shape.
/// </summary>
/// <param name="LicensePlate">Registration plate of the car.</param>
/// <param name="Model">Make and model of the car.</param>
/// <param name="Year">Year the car was built.</param>
/// <param name="Color">Exterior colour of the car.</param>
public sealed record CarV1(string LicensePlate, string Model, int Year, Color? Color);
