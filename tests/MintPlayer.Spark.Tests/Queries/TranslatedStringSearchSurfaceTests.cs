using System.Reflection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Tests.Queries;

/// <summary>
/// <c>QueryExecutor.IsOnSearchSurface</c> for the per-language fields a <c>TranslatedString</c> fans out to
/// (commerce X17). The generator names them <c>{Prop}_{tag with '-' as '_'}</c>
/// (<c>IndexNaming.LanguageField</c>): <c>Description_nl</c>, <c>Description_zh_Hant</c>.
/// </summary>
/// <remarks>
/// Red before X17: the field was split on its <em>last</em> <c>_</c>, so <c>Description_zh_Hant</c> looked
/// for an attribute named <c>Description_zh</c>, found none, and the field silently dropped off search.
/// Reached by reflection: the method is private and its callers need a RavenDB query to get there.
/// </remarks>
public class TranslatedStringSearchSurfaceTests
{
    private static readonly MethodInfo IsOnSearchSurface = typeof(QueryExecutor).GetMethod(
        "IsOnSearchSurface", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("QueryExecutor.IsOnSearchSurface was renamed or removed.");

    private static readonly EntityTypeDefinition Surface = new()
    {
        Id = Guid.NewGuid(),
        Name = "Place",
        Attributes =
        [
            new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Description", DataType = "TranslatedString" },
            new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Short_Name", DataType = "TranslatedString" },
            new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Code", DataType = "string" },
            new EntityAttributeDefinition
            {
                Id = Guid.NewGuid(), Name = "Secret", DataType = "TranslatedString", ShowedOn = EShowedOn.PersistentObject,
            },
        ],
    };

    private static bool OnSurface(string field) => (bool)IsOnSearchSurface.Invoke(null, [Surface, field])!;

    [Theory]
    [InlineData("Description_nl")]
    [InlineData("Description_zh_Hant")]
    [InlineData("Description_sr_Latn_RS")]
    [InlineData("Short_Name_en")]
    [InlineData("Short_Name_zh_Hant")]
    [InlineData("Code")]
    public void A_language_field_of_a_query_attribute_is_searched(string field)
        => OnSurface(field).Should().BeTrue();

    [Theory]
    [InlineData("Code_nl")]                 // not a TranslatedString
    [InlineData("Secret_nl")]               // a TranslatedString that is not on the query surface
    [InlineData("Description_")]            // no tag
    [InlineData("Description_toolongtag1")] // "toolongtag1" is not a well-formed tag (subtags are 1-8 characters)
    [InlineData("Unknown_nl")]
    public void Anything_else_is_not(string field)
        => OnSurface(field).Should().BeFalse();
}
