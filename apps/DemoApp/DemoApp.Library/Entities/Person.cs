using MintPlayer.Spark.Abstractions;
using System.ComponentModel;

namespace DemoApp.Library.Entities;

public class Person
{
    /// <summary>Unique identifier of this person, assigned automatically when it is saved.</summary>
    public string? Id { get; set; }
    /// <summary>Given name, as it should appear on badges and in greetings.</summary>
    public string FirstName { get; set; } = string.Empty;
    /// <summary>Family name or surname of the person.</summary>
    public string LastName { get; set; } = string.Empty;
    /// <summary>Email address used to contact the person.</summary>
    public string Email { get; set; } = string.Empty;
    /// <summary>Date on which the person was born; leave empty if unknown.</summary>
    public DateOnly? DateOfBirth { get; set; }

    /// <summary>
    /// The <see cref="Entities.Company"/> this person works for. Pick from the companies list;
    /// leave empty for freelancers.
    /// </summary>
    [Reference(typeof(Company), "GetCompanies")]
    public string? Company { get; set; }

    /// <summary>Home or postal address of the person, with street, city and state.</summary>
    public Address? Address { get; set; }

    [Description("Whether this person can sign in and appears in the default people lists.")]
    public bool IsActive { get; set; }
}
