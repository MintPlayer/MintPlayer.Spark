#if (UseModeration)
using MintPlayer.Spark.Moderation;

#endif
namespace SparkApp.Library.Entities;

#if (UseModeration)
/// <summary>A person. Replace it with your own entities: each public class here becomes a model type.</summary>
/// <remarks>Moderatable: users vote on and flag people, and the author and posting time are generated from the interface.</remarks>
public partial class Person : IModeratable
#else
/// <summary>A person. Replace it with your own entities: each public class here becomes a model type.</summary>
public class Person
#endif
{
    /// <summary>Unique identifier, assigned when the person is first saved.</summary>
    public string? Id { get; set; }

    /// <summary>Given name, as it should appear in greetings.</summary>
    public string FirstName { get; set; } = string.Empty;

    /// <summary>Family name.</summary>
    public string LastName { get; set; } = string.Empty;

    /// <summary>Email address used to contact the person.</summary>
    public string? Email { get; set; }

    /// <summary>Date of birth; leave empty if unknown.</summary>
    public DateOnly? DateOfBirth { get; set; }
}
