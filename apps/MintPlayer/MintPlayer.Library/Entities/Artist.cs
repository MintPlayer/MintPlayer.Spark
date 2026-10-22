using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Entities;

/// <summary>
/// A musical artist or band. The songs credited to an artist are a sub-query over <see cref="Song.Artists"/>.
/// </summary>
public partial class Artist : Subject
{
    public string Name { get; set; } = string.Empty;
    public int? YearStarted { get; set; }
    public int? YearQuit { get; set; }
    public List<ArtistMember> Members { get; set; } = [];
}

/// <summary>A person's membership of a band (legacy <c>ArtistPerson</c>).</summary>
[ValueObject]
public partial class ArtistMember
{
    [Reference(typeof(Person))]
    public string? PersonId { get; set; }

    /// <summary>Whether the person is a current member.</summary>
    public bool Active { get; set; }
}
