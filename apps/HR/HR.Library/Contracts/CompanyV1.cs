namespace HR.Contracts;

/// <summary>
/// Version 1 of the <c>companies</c> replication contract HR publishes. Fleet's replica type
/// (<c>Fleet.Replicated.Company</c>) has this shape.
/// </summary>
/// <param name="Name">Legal or trading name of the company.</param>
/// <param name="Website">Public website address of the company.</param>
/// <param name="EmployeeCount">Number of employees the company has.</param>
public sealed record CompanyV1(string Name, string? Website, int EmployeeCount);
