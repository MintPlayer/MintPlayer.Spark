namespace DemoApp.Library.Entities;

public class Company
{
    /// <summary>Unique identifier of this company, assigned automatically when it is saved.</summary>
    public string? Id { get; set; }
    /// <summary>Official name of the company as it should appear in lists and on people's records.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Public website address of the company, including <c>https://</c>.</summary>
    public string? Website { get; set; }
    /// <summary>Approximate number of people employed by the company; leave empty if unknown.</summary>
    public int? EmployeeCount { get; set; }
}
