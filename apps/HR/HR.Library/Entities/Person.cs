using MintPlayer.Spark.Abstractions;

namespace HR.Entities;

// The breadcrumb template lives in App_Data/Model/Person.json ("{FirstName} {LastName} @
// {Company}") and recurses through references: {Company} renders the Company's breadcrumb,
// which in turn renders its {Sector} (a Profession) — a 3-level chain.
//
// [GenerateIndex] replaces the previously hand-written People_Overview: the FullName concat the
// hand-written map computed is now an ordinary computed property (persisted, searchable), the
// complex Address column rides the AddressSort breadcrumb companion, and Jobs (a collection of
// complex elements) is stored-not-indexed (SPARK_INDEX_010 — expected).
[GenerateIndex]
public class Person
{
    /// <summary>Unique identifier of the person, assigned automatically on creation.</summary>
    public string? Id { get; set; }
    /// <summary>Given name of the person.</summary>
    public string FirstName { get; set; } = string.Empty;
    /// <summary>Family name of the person.</summary>
    public string LastName { get; set; } = string.Empty;

    [Search]
    public string FullName => $"{FirstName} {LastName}";
    /// <summary>E-mail address used to contact the person.</summary>
    public string? Email { get; set; }
    /// <summary>Date on which the person was born.</summary>
    public DateOnly? DateOfBirth { get; set; }

    /// <summary>The company the person currently works for.</summary>
    [Reference(typeof(Company))]
    public string? Company { get; set; }

    // Multi-reference: a person can hold several professions. Renders as a searchable
    // multi-select (bs-tree-select) on the edit form and as chips on detail/list.
    /// <summary>All professions the person holds; several can be selected.</summary>
    [Reference(typeof(Profession))]
    public List<string> Professions { get; set; } = [];

    /// <summary>Home address of the person.</summary>
    public Address? Address { get; set; }

    // [Sortable]: career history is an ordered list — drag-reorder the rows (order = array
    // position, no index field). Already editMode "inline", so this exercises inline + drag.
    /// <summary>Career history of the person, ordered by dragging rows into place.</summary>
    [Sortable]
    public CarreerJob[] Jobs { get; set; } = [];
}
