using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Prerendering;

/// <summary>
/// The one builder of an object's page URL (#602): the slug, the default <c>/po/{type}/{id}/{slug}</c>, a type's route
/// template, and the reverse match the prerender's 301 and the sitemap rely on.
/// </summary>
public class SparkEntityLinksTests
{
    private static readonly EntityTypeDefinition Song = new() { Id = Guid.Parse("11111111-1111-1111-1111-111111111111"), Name = "Song", Alias = "song" };
    private static readonly EntityTypeDefinition Tag = new() { Id = Guid.Parse("22222222-2222-2222-2222-222222222222"), Name = "Tag", Alias = "tag", Slug = false };
    private static readonly EntityTypeDefinition Artist = new() { Id = Guid.Parse("33333333-3333-3333-3333-333333333333"), Name = "Artist", Alias = "artist", Route = "artists/{id}/{slug}" };

    private static SparkEntityLinks Links(params EntityTypeDefinition[] types)
    {
        var models = Substitute.For<IModelLoader>();
        models.GetEntityTypes().Returns(types);
        foreach (var type in types)
            models.GetEntityType(type.Id).Returns(type);
        return new SparkEntityLinks(models);
    }

    [Theory]
    [InlineData("Beyoncé - Halo!", "beyonce-halo")]
    [InlineData("A Sky Full of Stars", "a-sky-full-of-stars")]
    [InlineData("  --Mötley Crüe--  ", "motley-crue")]
    [InlineData("AC/DC", "ac-dc")]
    [InlineData("99 Luftballons", "99-luftballons")]
    [InlineData("!!!", "")]
    [InlineData(null, "")]
    public void The_slug_is_lower_case_ascii_with_dashes(string? breadcrumb, string expected)
        => Links().Slug(breadcrumb).Should().Be(expected);

    [Fact]
    public void The_default_path_carries_the_alias_the_encoded_id_and_the_slug()
        => Links(Song).DetailPath(Song, "Songs/23", "Coldplay - A Sky Full of Stars")
            .Should().Be("/po/song/Songs%2F23/coldplay-a-sky-full-of-stars");

    [Theory]
    [InlineData(null)]
    [InlineData("!!!")]
    [InlineData("Edit")]
    public void There_is_no_slug_when_it_is_empty_or_edit(string? breadcrumb)
        => Links(Song).DetailPath(Song, "Songs/1", breadcrumb).Should().Be("/po/song/Songs%2F1");

    [Fact]
    public void A_type_can_turn_slugs_off()
        => Links(Tag).DetailPath(Tag, "Tags/1", "Rock").Should().Be("/po/tag/Tags%2F1");

    [Fact]
    public void A_route_template_shapes_the_path()
        => Links(Artist).DetailPath(Artist, "Artists/7", "Daft Punk").Should().Be("/artists/Artists%2F7/daft-punk");

    [Fact]
    public void A_loaded_object_gets_its_path_and_one_without_an_id_none()
    {
        var links = Links(Song);
        links.DetailPath(new PersistentObject { Id = "Songs/2", Name = "x", ObjectTypeId = Song.Id, Breadcrumb = "Halo" }).Should().Be("/po/song/Songs%2F2/halo");
        links.DetailPath(new PersistentObject { Id = null, Name = "x", ObjectTypeId = Song.Id }).Should().BeNull();
        links.DetailPath(new PersistentObject { Id = "X/1", Name = "x", ObjectTypeId = Guid.NewGuid() }).Should().BeNull();
    }

    [Theory]
    [InlineData("/po/song/Songs%2F23", "song", "Songs/23", null)]
    [InlineData("/po/song/Songs%2F23/old-slug", "song", "Songs/23", "old-slug")]
    [InlineData("/artists/Artists%2F7/daft-punk", "artist", "Artists/7", "daft-punk")]
    [InlineData("/ARTISTS/Artists%2F7", "artist", "Artists/7", null)]
    public void A_detail_path_matches(string path, string type, string id, string? slug)
        => Links(Song, Artist).Match(path).Should().Be(new MintPlayer.Spark.Abstractions.Links.SparkEntityPathMatch(type, id, slug));

    [Theory]
    [InlineData("/po/song/new")]
    [InlineData("/po/song/Songs%2F1/edit")]
    [InlineData("/po/song")]
    [InlineData("/query/songs")]
    [InlineData("/artists/Artists%2F7/edit")]
    [InlineData("/artists")]
    public void Other_pages_do_not_match(string path)
        => Links(Song, Artist).Match(path).Should().BeNull();

    [Theory]
    [InlineData("songs/{slug}", "{id}")]
    [InlineData("songs/{id}/{id}", "{id}")]
    [InlineData("songs/{slug}/{id}", "{slug}")]
    [InlineData("songs/x{id}", "whole placeholder")]
    [InlineData("po/{type}/{id}", "repeats the default")]
    public void An_invalid_template_is_refused_naming_the_type(string template, string why)
    {
        var act = () => SparkEntityLinks.ParseTemplate("Song", template);
        act.Should().Throw<InvalidOperationException>().WithMessage($"*'Song'*{why}*");
    }
}
