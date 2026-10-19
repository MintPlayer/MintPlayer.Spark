namespace HR.Contracts;

/// <summary>
/// Version 1 of the <c>people</c> replication contract HR publishes: the fields of a person other
/// modules may read. HR generates its ETL transform from the projection in <c>Program.cs</c>;
/// Fleet's replica type (<c>Fleet.Replicated.Person</c>) has this shape.
/// </summary>
/// <param name="FirstName">Given name of the person.</param>
/// <param name="LastName">Family name of the person.</param>
/// <param name="Email">Work e-mail address of the person.</param>
public sealed record PersonV1(string FirstName, string LastName, string? Email);
