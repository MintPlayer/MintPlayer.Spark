using MintPlayer.Entities;
using MintPlayer.Migration.Model;
using MintPlayer.Migration.Target;
using MintPlayer.Migration.Transform;
using MintPlayer.Migration.Verify;
using MintPlayer.Spark.Authorization.Identity;
using Newtonsoft.Json.Linq;

namespace MintPlayer.Migration.Tests;

public class TransformerTests
{
    private static readonly Guid Admin = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Plain = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid RoleAdmin = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset MigratedAt = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTime Min = DateTime.MinValue;

    private static TransformOptions Options(bool hash = false) => new()
    {
        TimeZone = LegacyTime.Resolve("Europe/Amsterdam"),
        MigratedAt = MigratedAt,
        HashRecoveryCodes = hash,
        KnownGroups = new HashSet<string> { "Administrator", "Blogger", "Everyone" },
    };

    private static LegacySubject Subject(int id, string type, Guid? userDeleteId = null, DateTime? insert = null, DateTime? update = null, DateTime? delete = null,
        string? name = null, string? title = null, DateTime? released = null, DateTime? born = null)
        => new(id, type, userDeleteId, insert ?? new DateTime(2021, 7, 1, 12, 0, 0), update, delete, name, null, null,
            type == "person" ? "First" : null, type == "person" ? "Last" : null, born, null, title, released);

    private static LegacySnapshot Snapshot() => new()
    {
        Users =
        [
            new LegacyUser(Admin, "admin", "ADMIN", "Admin@Example.com", "ADMIN@EXAMPLE.COM", true, "AQAAAAIAAYagAAAAE-hash", "STAMP", "CS1", null, false, true, null, true, 0, "", false),
            new LegacyUser(Plain, "plain", "PLAIN", "plain@example.com", "PLAIN@EXAMPLE.COM", false, null, "STAMP2", null, null, false, false, null, true, 0, "", true),
        ],
        Roles = [new LegacyRole(RoleAdmin, "Administrator")],
        UserRoles = [new LegacyUserRole(Admin, RoleAdmin)],
        UserLogins = [new LegacyUserLogin(Plain, "Google", "g-123", "Google")],
        UserTokens =
        [
            new LegacyUserToken(Admin, "[AspNetUserStore]", "AuthenticatorKey", "JBSWY3DPEHPK3PXP"),
            new LegacyUserToken(Admin, "[AspNetUserStore]", "RecoveryCodes", "AAAA-1111;BBBB-2222"),
        ],
        MediumTypes =
        [
            new LegacyMediumType(1, "YouTube", Guid.Empty, true),
            new LegacyMediumType(15, "Songteksten", Guid.Empty, false),
            new LegacyMediumType(7, "Old", Admin, true),
        ],
        TagCategories = [new LegacyTagCategory(1, unchecked((int)0x80FF0000), "Genre", Guid.Empty, new DateTime(2020, 2, 1), null, null)],
        Tags =
        [
            new LegacyTag(1, "Pop", 1, 0, Guid.Empty, new DateTime(2020, 2, 1), null, null),
            new LegacyTag(2, "Synth-pop", 1, 1, Guid.Empty, new DateTime(2020, 2, 1), null, null),
            new LegacyTag(3, "Loose", 0, 0, Admin, new DateTime(2020, 2, 1), null, new DateTime(2020, 3, 1)),
        ],
        Subjects =
        [
            Subject(10, "artist", name: "Band"),
            Subject(11, "artist", userDeleteId: Admin, delete: new DateTime(2022, 1, 1), name: "Gone"),
            Subject(20, "person", insert: Min, update: new DateTime(2021, 1, 1, 10, 0, 0), born: new DateTime(1980, 5, 3, 22, 0, 0)),
            Subject(30, "song", title: "Hit", released: Min),
            Subject(31, "song", title: "Other", delete: new DateTime(2022, 1, 1), insert: Min),
        ],
        ArtistPersons = [new LegacyArtistPerson(10, 20, true)],
        ArtistSongs = [new LegacyArtistSong(10, 30, true), new LegacyArtistSong(11, 30, false)],
        SubjectTags = [new LegacySubjectTag(30, 2), new LegacySubjectTag(30, 1), new LegacySubjectTag(30, 1)],
        Media =
        [
            new LegacyMedium(2, 15, 30, "https://genius.com/hit"),
            new LegacyMedium(1, 1, 30, "https://en.wikipedia.org/wiki/Hit"),
            new LegacyMedium(3, 1, 30, "https://www.youtube.com/watch?v=hit"),
            new LegacyMedium(4, 1, 30, "  "),
        ],
        Lyrics =
        [
            new LegacyLyrics(30, Admin, "l1\n\nl2\nl3", new DateTime(2021, 1, 1), "[20,40,60]"),
            new LegacyLyrics(30, Plain, "l1\n\nl2\nl3", new DateTime(2022, 1, 1), "[20]"),
        ],
        Likes = [new LegacyLike(30, Plain, true), new LegacyLike(10, Plain, true), new LegacyLike(20, Plain, false)],
        Playlists =
        [
            new LegacyPlaylist(5, Plain, "Summer", false, 1),
            new LegacyPlaylist(6, Plain, "Old", true, 0),
        ],
        PlaylistSongs = [new LegacyPlaylistSong(5, 31, 2), new LegacyPlaylistSong(5, 30, 1), new LegacyPlaylistSong(5, 30, 3)],
        BlogPosts = [new LegacyBlogPost(4, "Title", "Head", "Body", Admin, Guid.Empty, new DateTime(2021, 1, 1, 10, 0, 0), null, null)],
        DroppedTableCounts = new() { ["Jobs"] = 629, ["LogEntries"] = 0 },
    };

    private static T Doc<T>(MigrationPlan plan, string id) => (T)plan.Documents.Single(d => d.Id == id).Entity;

    [Fact]
    public void Preflight_accepts_a_consistent_snapshot()
        => (Preflight.Check(Snapshot(), Options().KnownGroups)).Should().BeEmpty();

    [Fact]
    public void Preflight_rejects_broken_snapshots()
    {
        var s = Snapshot();
        s.Subjects.Add(Subject(99, "album"));
        s.Tags.Add(new LegacyTag(8, "x", 1, 9, Guid.Empty, DateTime.Now, null, null));
        s.Tags.Add(new LegacyTag(9, "y", 1, 8, Guid.Empty, DateTime.Now, null, null));
        s.Users.Add(s.Users[0] with { Id = Guid.NewGuid(), NormalizedUserName = "OTHER" });
        s.Lyrics.Add(new LegacyLyrics(10, Admin, "x", DateTime.Now, null));
        s.PlaylistSongs.Add(new LegacyPlaylistSong(5, 404, 9));
        var errors = Preflight.Check(s, Options().KnownGroups);
        errors.Should().Contain(e => e.Contains("unknown discriminator"));
        errors.Should().Contain(e => e.Contains("parent cycle"));
        errors.Should().Contain(e => e.Contains("normalised email"));
        errors.Should().Contain(e => e.StartsWith("Lyrics: song 10"));
        errors.Should().Contain(e => e.Contains("song 404"));
    }

    [Fact]
    public void Preflight_rejects_roles_that_are_not_groups()
    {
        var s = Snapshot();
        s.Roles[0] = s.Roles[0] with { Name = "Editor" };
        (Preflight.Check(s, Options().KnownGroups)).Should().Contain(e => e.Contains("not a security.json group"));
    }

    [Fact]
    public void Soft_delete_and_audit_follow_s2_rules()
    {
        var plan = LegacyTransformer.Transform(Snapshot(), Options());
        var gone = Doc<Artist>(plan, "Artists/11");
        gone.IsDeleted.Should().BeTrue();
        gone.DeletedAt.Should().Be(new DateTimeOffset(2021, 12, 31, 23, 0, 0, TimeSpan.Zero));

        var bare = Doc<Song>(plan, "Songs/31"); // DateDelete without user → live
        bare.IsDeleted.Should().BeFalse();
        bare.DeletedAt.Should().NotHaveValue();
        bare.CreatedAt.Should().Be(MigratedAt); // 0001-01-01, no DateUpdate → migration time
        plan.Issues.Should().Contain(i => i.Code == "bare-datedelete-live" && i.Subject == "Songs/31");

        var person = Doc<Person>(plan, "People/20");
        person.CreatedAt.Should().Be(new DateTimeOffset(2021, 1, 1, 9, 0, 0, TimeSpan.Zero)); // fallback to DateUpdate (CET)
        person.Born.Should().Be(new DateOnly(1980, 5, 4));

        var hit = Doc<Song>(plan, "Songs/30");
        hit.CreatedAt.Should().Be(new DateTimeOffset(2021, 7, 1, 10, 0, 0, TimeSpan.Zero)); // CEST
        hit.Released.Should().NotHaveValue();

        (Doc<MediumType>(plan, "MediumTypes/1").IsDeleted).Should().BeFalse(); // zero GUID = live
        (Doc<MediumType>(plan, "MediumTypes/7").IsDeleted).Should().BeTrue();
        (Doc<Tag>(plan, "Tags/1").IsDeleted).Should().BeFalse();
        (Doc<Tag>(plan, "Tags/3").IsDeleted).Should().BeTrue();
    }

    [Fact]
    public void Catalog_references_and_embedded_rows()
    {
        var plan = LegacyTransformer.Transform(Snapshot(), Options());
        var hit = Doc<Song>(plan, "Songs/30");
        (hit.Media.Select(m => m.Value)).Should().Equal(["https://en.wikipedia.org/wiki/Hit", "https://genius.com/hit", "https://www.youtube.com/watch?v=hit"]);
        (hit.Media.Select(m => m.TypeId)).Should().Equal(["MediumTypes/1", "MediumTypes/15", "MediumTypes/1"]);
        plan.Issues.Should().Contain(i => i.Code == "medium-empty" && i.Subject == "Media/4");
        hit.TagIds.Should().Equal(["Tags/1", "Tags/2"]); // dedup + ordered
        (hit.Artists.Select(a => (a.ArtistId!, a.Credited))).Should().Equal(new[] { ("Artists/10", true), ("Artists/11", false) });
        (Doc<Artist>(plan, "Artists/10").Members.Select(m => (m.PersonId!, m.Active))).Should().Equal(new[] { ("People/20", true) });

        var tag2 = Doc<Tag>(plan, "Tags/2");
        tag2.CategoryId.Should().Be("TagCategories/1");
        tag2.ParentId.Should().Be("Tags/1");
        var tag3 = Doc<Tag>(plan, "Tags/3");
        tag3.CategoryId.Should().BeNull();
        tag3.ParentId.Should().BeNull();

        var cat = Doc<TagCategory>(plan, "TagCategories/1");
        cat.Color.A.Should().Be(255);
        plan.Issues.Should().Contain(i => i.Code == "color-alpha-dropped");

        var hidden = Doc<MediumType>(plan, "MediumTypes/15");
        hidden.Name.Should().Be("Songteksten");
        hidden.Visible.Should().BeFalse();
    }

    [Fact]
    public void Lyrics_collapse_to_one_text_and_timing_keyed_to_first_playable_medium()
    {
        var hit = Doc<Song>(LegacyTransformer.Transform(Snapshot(), Options()), "Songs/30");
        hit.Lyrics.Should().Be("l1\n\nl2\nl3");
        hit.LyricsTimings.Should().ContainSingle();
        var timing = hit.LyricsTimings.Single();
        timing.MediumUrl.Should().Be("https://www.youtube.com/watch?v=hit");
        timing.StartTimes.Should().Equal(new double?[] { 1.0, null, 2.0, 3.0 }); // older row had the complete timeline
    }

    [Fact]
    public void Likes_group_per_user()
    {
        var plan = LegacyTransformer.Transform(Snapshot(), Options());
        var userId = Ids.User(Plain);
        var likes = Doc<MintPlayer.Entities.UserLike>(plan, $"UserLikes/{userId}");
        likes.UserId.Should().Be(userId);
        likes.Likes.Should().Equal(["Artists/10", "Songs/30"]);
        likes.Dislikes.Should().Equal(["People/20"]);
    }

    [Fact]
    public void Playlists_keep_order_and_duplicates()
    {
        var plan = LegacyTransformer.Transform(Snapshot(), Options());
        var p = Doc<Playlist>(plan, "Playlists/5");
        p.Name.Should().Be("Summer");
        p.IsPublic.Should().BeTrue();
        p.OwnerId.Should().Be(Ids.User(Plain));
        (p.Tracks.Select(t => t.SongId)).Should().Equal(["Songs/30", "Songs/31", "Songs/30"]);
        (Doc<Playlist>(plan, "Playlists/6").IsDeleted).Should().BeTrue();
    }

    [Fact]
    public void Blog_posts_map_onto_the_entity()
    {
        var plan = LegacyTransformer.Transform(Snapshot(), Options());
        var post = Doc<BlogPost>(plan, "BlogPosts/4");
        post.AuthorId.Should().Be(Ids.User(Admin));
        post.Published.Should().Be(new DateTimeOffset(2021, 1, 1, 9, 0, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Users_keep_credentials_and_roles(bool hash)
    {
        var plan = LegacyTransformer.Transform(Snapshot(), Options(hash));
        var admin = Doc<SparkUser>(plan, Ids.User(Admin));
        admin.PasswordHash.Should().Be("AQAAAAIAAYagAAAAE-hash");
        admin.SecurityStamp.Should().Be("STAMP");
        admin.AuthenticatorKey.Should().Be("JBSWY3DPEHPK3PXP");
        admin.TwoFactorEnabled.Should().BeTrue();
        admin.TwoFactorRecoveryCodes.Should().Equal(hash ? new[] { RecoveryCodes.Hash("AAAA-1111"), RecoveryCodes.Hash("BBBB-2222") } : new[] { "AAAA-1111", "BBBB-2222" });
        admin.Roles.Should().Equal(["Administrator"]);
        admin.Tokens.Should().BeEmpty();

        var plain = Doc<SparkUser>(plan, Ids.User(Plain));
        plain.BypassTwoFactorForExternalLogin.Should().BeTrue();
        ((plain.Logins.Single().LoginProvider, plain.Logins.Single().ProviderKey)).Should().Be(("Google", "g-123"));

        plan.CompareExchange["emails/admin@example.com"].Should().Be(Ids.User(Admin));
        plan.CompareExchange.Count.Should().Be(2);
    }

    [Fact]
    public void Every_document_id_starts_with_its_collection()
        => LegacyTransformer.Transform(Snapshot(), Options()).Documents.Should().OnlyContain(d => d.Id.StartsWith(d.Collection + "/"));

    [Fact]
    public void Reference_walker_finds_every_reference_kind()
    {
        var plan = LegacyTransformer.Transform(Snapshot(), Options());
        var serializer = SparkStore.CreateComparisonSerializer();
        var refs = plan.Documents
            .SelectMany(d => Reconciler.References(JObject.FromObject(d.Entity, serializer), d.Entity is MintPlayer.Entities.UserLike))
            .Select(r => r.Reference).ToHashSet();
        foreach (var expected in new[] { "MediumTypes/15", "Tags/1", "TagCategories/1", "People/20", "Artists/11", "Songs/31", Ids.User(Plain), Ids.User(Admin) })
            refs.Should().Contain(expected);
        var ids = plan.Documents.Select(d => d.Id).ToHashSet();
        refs.Should().OnlyContain(r => ids.Contains(r)); // the fixture is referentially complete
    }
}
