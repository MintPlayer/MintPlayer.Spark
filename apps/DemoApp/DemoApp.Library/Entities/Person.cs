using MintPlayer.Spark.Abstractions;
using System.ComponentModel;

namespace DemoApp.Library.Entities;

public class Person
{
    public string? Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public DateOnly? DateOfBirth { get; set; }

    /// <summary>
    /// The <see cref="Entities.Company"/> this person works for. Pick from the companies list;
    /// leave empty for freelancers.
    /// </summary>
    [Reference(typeof(Company), "GetCompanies")]
    public string? Company { get; set; }

    public Address? Address { get; set; }

    [Description("Whether this person can sign in and appears in the default people lists.")]
    public bool IsActive { get; set; }
}
