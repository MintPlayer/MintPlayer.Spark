using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Model;
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

/// <summary>
/// #384 — the same guarantee as <see cref="EntityMapperAsDetailBreadcrumbTests"/>, but for a row
/// type that carries a <b>row key</b>.
/// <para>
/// This is the case the sibling fixture cannot reach. Its <c>SongArtist</c> is keyless, so
/// <c>po.Id</c> comes back null and the mapper's embedded-render branch fires whatever the branch
/// condition happens to say about ids. Since #382 every <c>[ValueObject]</c> is keyed — the
/// generator mints an <c>Id</c> in a field initializer that runs during deserialization — so the
/// keyed shape is now the default one, and it was the only shape not under test.
/// </para>
/// <para>
/// A row key is never a key in <see cref="BreadcrumbResult"/>: that map holds document ids, which
/// the resolver reads from a property literally named <c>Id</c>. An embedded row is therefore
/// absent from it because it is embedded — not because it is unkeyed — and the mapper must still
/// render the row's own template.
/// </para>
/// </summary>
public class EntityMapperKeyedAsDetailBreadcrumbTests
{
    private static readonly Guid AlbumTypeId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    /// <summary>A keyed embedded row — the shape <c>ValueObjectKeyGenerator</c> now produces.</summary>
    private sealed class KeyedCredit
    {
        public string Id { get; set; } = "3f2a9c11e4b7";
        public string? ArtistId { get; set; }
        public string? Role { get; set; }
    }

    private sealed class Album { public string? Id { get; set; } public string Title { get; set; } = ""; public List<KeyedCredit> Credits { get; set; } = []; }

    static EntityMapperKeyedAsDetailBreadcrumbTests()
        => SparkValueObjects.Register(typeof(KeyedCredit), nameof(KeyedCredit.Id), row => ((KeyedCredit)row).Id);

    private static EntityMapper MapperFor(string creditTemplate)
    {
        var modelLoader = Substitute.For<IModelLoader>();

        var creditDef = new EntityTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = "KeyedCredit",
            ClrType = typeof(KeyedCredit).FullName!,
            Breadcrumb = creditTemplate,
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "ArtistId", DataType = "Reference", ReferenceType = "Demo.Artist", Query = "GetArtists" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Role", DataType = "string" },
            ],
        };

        var albumDef = new EntityTypeDefinition
        {
            Id = AlbumTypeId,
            Name = "Album",
            ClrType = typeof(Album).FullName!,
            Breadcrumb = "{Title}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Title", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Credits", DataType = "AsDetail", AsDetailType = typeof(KeyedCredit).FullName, IsArray = true },
            ],
        };

        modelLoader.GetEntityType(AlbumTypeId).Returns(albumDef);
        modelLoader.GetEntityTypeByClrType(typeof(Album).FullName!).Returns(albumDef);
        modelLoader.GetEntityTypeByClrType(typeof(KeyedCredit).FullName!).Returns(creditDef);

        return new EntityMapper(modelLoader);
    }

    private static Album RepoAlbum() => new()
    {
        Id = "Albums/7",
        Title = "Everybody",
        Credits =
        [
            new KeyedCredit { Id = "a1b2c3", ArtistId = "Artists/40", Role = "Vocals" },
            new KeyedCredit { Id = "d4e5f6", ArtistId = "Artists/42", Role = "Producer" },
        ],
    };

    /// <summary>The row keys are deliberately absent — only document ids are ever in this map.</summary>
    private static BreadcrumbResult Resolved() => new(new Dictionary<string, string>
    {
        ["Albums/7"] = "Everybody",
        ["Artists/40"] = "Logic",
        ["Artists/42"] = "Khalid",
    });

    private static IReadOnlyList<string?> CreditBreadcrumbs(PersistentObject po)
        => po.Attributes.OfType<PersistentObjectAttributeAsDetail>().Single(a => a.Name == "Credits")
             .Objects!.Select(r => r.Breadcrumb).ToList();

    [Fact]
    public void Keyed_embedded_row_renders_a_reference_template_not_the_clr_type_name()
    {
        var po = MapperFor("{ArtistId}").ToPersistentObject(RepoAlbum(), AlbumTypeId, Resolved());

        // Before #384's fix this is ["KeyedCredit", "KeyedCredit"]: the row key makes po.Id
        // non-empty, the gate's IsNullOrEmpty(po.Id) term is false, and the renderer is skipped.
        CreditBreadcrumbs(po).Should().Equal("Logic", "Khalid");
    }

    [Fact]
    public void Keyed_embedded_row_renders_a_scalar_template()
    {
        // Not just reference resolution — a plain scalar token is skipped by the same gate.
        var po = MapperFor("{Role}").ToPersistentObject(RepoAlbum(), AlbumTypeId, Resolved());

        CreditBreadcrumbs(po).Should().Equal("Vocals", "Producer");
    }

    [Fact]
    public void The_row_key_is_still_carried_so_a_save_can_match_rows()
    {
        // Guards the fix against being "achieved" by reverting #382's key round-trip.
        var po = MapperFor("{Role}").ToPersistentObject(RepoAlbum(), AlbumTypeId, Resolved());

        po.Attributes.OfType<PersistentObjectAttributeAsDetail>().Single(a => a.Name == "Credits")
          .Objects!.Select(r => r.Id).Should().Equal("a1b2c3", "d4e5f6");
    }

    [Fact]
    public void A_root_object_still_takes_its_breadcrumb_from_the_resolved_result()
    {
        // FR3 — the root's id IS in the map, so it must be read from there and never re-rendered.
        var po = MapperFor("{Role}").ToPersistentObject(RepoAlbum(), AlbumTypeId, Resolved());

        po.Breadcrumb.Should().Be("Everybody");
    }

    [Fact]
    public void A_root_whose_resolved_breadcrumb_is_redacted_keeps_the_placeholder()
    {
        // FR3 — dropping the id term must not let the mapper re-render its way around redaction.
        var redacted = new BreadcrumbResult(new Dictionary<string, string>
        {
            ["Albums/7"] = "*****",
            ["Artists/40"] = "*****",
            ["Artists/42"] = "Khalid",
        });

        var po = MapperFor("{ArtistId}").ToPersistentObject(RepoAlbum(), AlbumTypeId, redacted);

        po.Breadcrumb.Should().Be("*****");
        // And the embedded rows resolve their reference tokens through the same map, so the
        // redaction survives into the row breadcrumb too.
        CreditBreadcrumbs(po).Should().Equal("*****", "Khalid");
    }
}
