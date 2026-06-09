using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Services.Breadcrumb;
using NSubstitute;

namespace MintPlayer.Spark.Tests;

/// <summary>
/// Pins #185 / #184 at the mapper boundary: once the resolver has resolved the documents an
/// AsDetail child references (keyed by id in the <see cref="BreadcrumbResult"/>), the mapper must
/// (a) copy that breadcrumb onto each embedded reference attribute — independent of any
/// options-page membership — and (b) render each embedded row's own <c>[Breadcrumb]</c> template
/// rather than the CLR type name.
/// </summary>
public class EntityMapperAsDetailBreadcrumbTests
{
    private static readonly Guid SongTypeId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private sealed class SongArtist { public string? ArtistId { get; set; } }
    private sealed class Song { public string? Id { get; set; } public string Title { get; set; } = ""; public List<SongArtist> Artists { get; set; } = []; }

    private readonly EntityMapper _mapper;

    public EntityMapperAsDetailBreadcrumbTests()
    {
        var modelLoader = Substitute.For<IModelLoader>();

        var songArtistDef = new EntityTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = "SongArtist",
            ClrType = typeof(SongArtist).FullName!,
            Breadcrumb = "{ArtistId}", // the row's own breadcrumb is the referenced artist's name
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "ArtistId", DataType = "Reference", ReferenceType = "Demo.Artist", Query = "GetArtists" },
            ],
        };

        var songDef = new EntityTypeDefinition
        {
            Id = SongTypeId,
            Name = "Song",
            ClrType = typeof(Song).FullName!,
            Breadcrumb = "{Title}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Title", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Artists", DataType = "AsDetail", AsDetailType = typeof(SongArtist).FullName, IsArray = true },
            ],
        };

        modelLoader.GetEntityType(SongTypeId).Returns(songDef);
        modelLoader.GetEntityTypeByClrType(typeof(Song).FullName!).Returns(songDef);
        modelLoader.GetEntityTypeByClrType(typeof(SongArtist).FullName!).Returns(songArtistDef);

        _mapper = new EntityMapper(modelLoader);
    }

    private static Song RepoSong() => new()
    {
        Id = "Songs/43",
        Title = "1-800-273-8255",
        Artists =
        [
            new SongArtist { ArtistId = "Artists/40" }, // Logic — would sort onto a later options page
            new SongArtist { ArtistId = "Artists/41" }, // Alessia Cara — first page
            new SongArtist { ArtistId = "Artists/42" }, // Khalid — later page
        ],
    };

    [Fact]
    public void Embedded_reference_attribute_gets_the_resolved_breadcrumb_regardless_of_page()
    {
        // The resolver descended into the AsDetail children and resolved all three artists by id.
        var breadcrumbs = new BreadcrumbResult(new Dictionary<string, string>
        {
            ["Songs/43"] = "1-800-273-8255",
            ["Artists/40"] = "Logic",
            ["Artists/41"] = "Alessia Cara",
            ["Artists/42"] = "Khalid",
        });

        var po = _mapper.ToPersistentObject(RepoSong(), SongTypeId, breadcrumbs);

        var artists = po.Attributes.OfType<PersistentObjectAttributeAsDetail>().Single(a => a.Name == "Artists");
        var rows = artists.Objects!;
        rows.Should().HaveCount(3);

        var artistIdBreadcrumbs = rows
            .Select(r => r.Attributes.Single(a => a.Name == "ArtistId").Breadcrumb)
            .ToList();
        artistIdBreadcrumbs.Should().Equal("Logic", "Alessia Cara", "Khalid");
    }

    [Fact]
    public void Embedded_row_renders_its_own_breadcrumb_template_not_the_clr_type_name()
    {
        var breadcrumbs = new BreadcrumbResult(new Dictionary<string, string>
        {
            ["Songs/43"] = "1-800-273-8255",
            ["Artists/40"] = "Logic",
            ["Artists/41"] = "Alessia Cara",
            ["Artists/42"] = "Khalid",
        });

        var po = _mapper.ToPersistentObject(RepoSong(), SongTypeId, breadcrumbs);

        var artists = po.Attributes.OfType<PersistentObjectAttributeAsDetail>().Single(a => a.Name == "Artists");
        var rowBreadcrumbs = artists.Objects!.Select(r => r.Breadcrumb).ToList();
        // {ArtistId} resolves through the referenced artist's breadcrumb — not "SongArtist".
        rowBreadcrumbs.Should().Equal("Logic", "Alessia Cara", "Khalid");
        rowBreadcrumbs.Should().NotContain("SongArtist");
    }
}
