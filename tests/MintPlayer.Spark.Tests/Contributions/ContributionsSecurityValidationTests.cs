using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Contributions;

/// <summary>
/// Contributions M5b: the runtime <c>security.json</c> validator accepts rights on the generated
/// contribution and current types (<c>CoSongLyricsContribution</c>, <c>CoSongLyricsCurrent</c>) while the
/// model is one build behind — the types exist as classes, registered as satellites of <c>CoSong</c>,
/// but synchronize has not written their model files yet. Synchronize itself has to start with those
/// rights granted. The attribute is still checked, against the class.
/// </summary>
public class ContributionsSecurityValidationTests
{
    private static readonly Guid Moderators = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static SecurityConfiguration Config(string resource) => new()
    {
        Groups = { [Moderators.ToString()] = "Moderators" },
        Rights = [new Right { Key = Guid.NewGuid().ToString(), GroupId = Moderators, Resource = resource }],
    };

    /// <summary>A model holding only the target (<c>CoSong</c>): the generated types have no model file yet.</summary>
    private static IModelLoader ModelWithoutTheGeneratedTypes()
    {
        var song = new EntityTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = "CoSong",
            ClrType = typeof(CoSong).AssemblyQualifiedName,
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Title" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Lyrics" },
            ],
        };
        var model = Substitute.For<IModelLoader>();
        model.GetEntityTypes().Returns(new[] { song });
        model.GetEntityTypeByName(Arg.Any<string>())
            .Returns(ci => string.Equals(ci.Arg<string>(), "CoSong", StringComparison.OrdinalIgnoreCase) ? song : null);
        return model;
    }

    [Theory]
    [InlineData("Query/CoSongLyricsContribution/ContributorId")]
    [InlineData("QueryRead/CoSongLyricsContribution/Text")]
    [InlineData("read/cosonglyricscontribution/language")]
    [InlineData("Read/CoSongLyricsCurrent/Text")]
    [InlineData("RevertContribution/CoSongLyricsContribution")]
    [InlineData("Delete/CoSongLyricsCurrent")]
    public void A_right_on_a_generated_type_without_a_model_file_is_accepted(string resource)
    {
        var act = () => SecurityConfigurationValidator.Validate(Config(resource), ModelWithoutTheGeneratedTypes());

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("Query/CoSongLyricsContribution/Lyricz", "declares no attribute 'Lyricz'")]
    [InlineData("Delete/CoSongLyricsContribution/Text", "no attribute-level form")]
    [InlineData("Read/CoSongLyricsVersion/Text", "no persistent object named 'CoSongLyricsVersion'")]
    public void A_wrong_right_on_a_generated_type_is_still_refused(string resource, string expected)
    {
        var act = () => SecurityConfigurationValidator.Validate(Config(resource), ModelWithoutTheGeneratedTypes());

        act.Should().Throw<SparkSecurityConfigurationException>().Which.Message.Should().Contain(expected);
    }
}
