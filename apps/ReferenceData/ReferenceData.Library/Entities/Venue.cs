using MintPlayer.Spark.ReferenceData;

namespace ReferenceData.Library.Entities;

/// <summary>
/// A place where something happens: the consumer page's example (docs/reference_data_PRD.md §4.16). Its address is
/// entered with the reference-data address renderer and checked by the client package's Address behaviour.
/// </summary>
public class Venue
{
    /// <summary>Unique identifier of this venue, assigned automatically when it is saved.</summary>
    public string? Id { get; set; }

    /// <summary>The venue's name, as shown on tickets and event pages.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The venue's postal address; its time zone becomes the venue's (commerce D33).</summary>
    public Address? Address { get; set; }
}
