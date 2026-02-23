using MintPlayer.Spark.Abstractions;

namespace HR.Entities;

public class Person
{
    public string? Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public DateOnly? DateOfBirth { get; set; }

    [Reference(typeof(Company))]
    public string? Company { get; set; }

    public Address? Address { get; set; }
}
