using MintPlayer.Spark.Abstractions;

namespace DemoApp.Data;

public class Person
{
    public string? Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public DateOnly? DateOfBirth { get; set; }

    [Reference(typeof(Company), "GetCompanies")]
    public string? Company { get; set; }

    
    public Address? Address { get; set; }

    public bool IsActive { get; set; }

    public string FullName => $"{FirstName} {LastName}";
}
