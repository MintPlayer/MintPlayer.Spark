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
    public string? Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;

    [Search]
    public string FullName => $"{FirstName} {LastName}";
    public string? Email { get; set; }
    public DateOnly? DateOfBirth { get; set; }

    [Reference(typeof(Company))]
    public string? Company { get; set; }

    // Multi-reference: a person can hold several professions. Renders as a searchable
    // multi-select (bs-tree-select) on the edit form and as chips on detail/list.
    [Reference(typeof(Profession))]
    public List<string> Professions { get; set; } = [];

    public Address? Address { get; set; }

    // [Sortable]: career history is an ordered list — drag-reorder the rows (order = array
    // position, no index field). Already editMode "inline", so this exercises inline + drag.
    [Sortable]
    public CarreerJob[] Jobs { get; set; } = [];
}
