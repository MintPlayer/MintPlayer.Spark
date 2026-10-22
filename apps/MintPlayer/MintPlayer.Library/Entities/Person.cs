using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Entities;

/// <summary>
/// A real person: a band member or songwriter. The bands a person plays in are a sub-query over
/// <see cref="Artist.Members"/>, where the membership lives.
/// </summary>
/// <remarks>
/// <c>[GenerateIndex]</c> replaces the legacy hand-written <c>People_Overview</c>: its <c>FullName</c> concat is the
/// computed property below, persisted and full-text searchable.
/// </remarks>
[GenerateIndex]
public partial class Person : Subject
{
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;

    [Search]
    public string FullName => $"{FirstName} {LastName}";

    public DateOnly? Born { get; set; }
    public DateOnly? Died { get; set; }
}
