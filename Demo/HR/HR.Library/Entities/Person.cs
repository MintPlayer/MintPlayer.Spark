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

    // Multi-reference: a person can hold several professions. Renders as a searchable
    // multi-select (bs-tree-select) on the edit form and as chips on detail/list.
    [Reference(typeof(Profession))]
    public List<string> Professions { get; set; } = [];

    public Address? Address { get; set; }
    public CarreerJob[] Jobs { get; set; } = [];
}
