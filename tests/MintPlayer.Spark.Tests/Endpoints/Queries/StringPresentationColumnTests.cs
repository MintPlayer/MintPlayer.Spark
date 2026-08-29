using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;

namespace MintPlayer.Spark.Tests.Endpoints.Queries;

/// <summary>
/// #327 §9.1 — the <c>image</c> and <c>url</c> data types, end to end through the real endpoint.
/// <para>
/// These are <b>presentation-only overrides of a string property</b>: the CLR type cannot express
/// "this string is an image address", so the value is hand-authored in the model file. Two things
/// therefore have to hold, and neither is visible from the client tests that render them — the
/// server must ship the declared <c>dataType</c> to the wire <em>verbatim</em>, and the value must
/// arrive as an ordinary string beside it. Normalising, lower-casing or falling back to
/// <c>"string"</c> anywhere in the projector would leave the grid rendering plain text with nothing
/// to say why.
/// </para>
/// </summary>
public class StringPresentationColumnTests : SparkTestDriver
{
    private static readonly Guid GalleryTypeId = Guid.Parse("1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d");
    private static readonly Guid GalleryQueryId = Guid.Parse("2b3c4d5e-6f7a-4b8c-9d0e-1f2a3b4c5d6e");

    /// <summary>
    /// A composed type, so the test needs no documents and no entity class — the subject is the
    /// column metadata, not where the rows came from.
    /// </summary>
    private static EntityTypeFile GalleryModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = GalleryTypeId,
            Name = "Gallery",
            Breadcrumb = "{Title}",
            Attributes =
            [
                new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(), Name = "Title", DataType = "string",
                    ShowedOn = EShowedOn.Query | EShowedOn.PersistentObject,
                },
                new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(), Name = "Thumbnail", DataType = "image",
                    ShowedOn = EShowedOn.Query | EShowedOn.PersistentObject,
                },
                new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(), Name = "Homepage", DataType = "url",
                    ShowedOn = EShowedOn.Query | EShowedOn.PersistentObject,
                },
            ],
        },
        Queries =
        [
            new SparkQuery
            {
                Id = GalleryQueryId,
                Name = "GalleryRows",
                Source = "Custom.GetRows",
                EntityType = "Gallery",
            },
        ],
    };

    private static async Task<QueryResult> ExecuteAsync(SparkEndpointFactory factory)
    {
        using var client = new SparkClient(factory.CreateClient(), ownsClient: true);
        return await client.ExecuteQueryAsync(GalleryQueryId);
    }

    [Fact]
    public async Task The_declared_dataType_reaches_the_wire_verbatim()
    {
        await using var factory = new SparkEndpointFactory(Store, [GalleryModel()]);

        var result = await ExecuteAsync(factory);

        // Case included: the client dispatches on `dataType === 'image'`, so a server that
        // title-cased or normalised these would render plain text with nothing to say why.
        result.Columns.Single(c => c.Name == "Thumbnail").DataType.Should().Be("image");
        result.Columns.Single(c => c.Name == "Homepage").DataType.Should().Be("url");
        result.Columns.Single(c => c.Name == "Title").DataType.Should().Be("string");
    }

    [Fact]
    public async Task The_values_arrive_as_ordinary_strings_beside_them()
    {
        await using var factory = new SparkEndpointFactory(Store, [GalleryModel()]);

        var result = await ExecuteAsync(factory);

        var row = result.Items.Single(i => i.Id == "gallery/1");
        row.Values.Single(v => v.Key == "Thumbnail").Value!.ToString()
            .Should().Be("https://cdn.example.com/1.png");
        row.Values.Single(v => v.Key == "Homepage").Value!.ToString()
            .Should().Be("https://example.com/one");
    }

    [Fact]
    public async Task A_null_presentation_value_is_carried_rather_than_dropped()
    {
        // The client's image and url branches both test the value and render nothing when it is
        // empty. That only works if the cell is present with a null value — a dropped key would
        // make "no image" indistinguishable from "column not in this result".
        await using var factory = new SparkEndpointFactory(Store, [GalleryModel()]);

        var result = await ExecuteAsync(factory);

        var row = result.Items.Single(i => i.Id == "gallery/2");
        row.Values.Should().Contain(v => v.Key == "Thumbnail");
        row.Values.Single(v => v.Key == "Thumbnail").Value.Should().BeNull();
    }

    [Fact]
    public async Task A_presentation_column_is_an_ordinary_column_in_every_other_respect()
    {
        // No special-casing crept in: these still carry their label, order and array-ness like any
        // other column, so the grid lays them out normally.
        await using var factory = new SparkEndpointFactory(Store, [GalleryModel()]);

        var result = await ExecuteAsync(factory);

        var thumbnail = result.Columns.Single(c => c.Name == "Thumbnail");
        thumbnail.IsArray.Should().BeFalse();
        thumbnail.ReferenceType.Should().BeNull();
        thumbnail.AsDetailType.Should().BeNull();
        result.Columns.Should().HaveCount(3);
    }
}

/// <summary>Found by name — the composed-query seam. Rows are computed; nothing is stored.</summary>
public sealed class GalleryActions
{
    public IEnumerable<GalleryRow> GetRows() =>
    [
        new GalleryRow("gallery/1", "One", "https://cdn.example.com/1.png", "https://example.com/one"),
        new GalleryRow("gallery/2", "Two", null, null),
    ];
}

public sealed record GalleryRow(string Id, string Title, string? Thumbnail, string? Homepage);
