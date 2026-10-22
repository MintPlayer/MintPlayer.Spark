using MintPlayer.Entities;
using MintPlayer.Spark;
using Raven.Client.Documents.Linq;

namespace MintPlayer;

/// <summary>The catalog (people, artists, songs, their tags and medium types) and the users' playlists.</summary>
public partial class MintPlayerContext : SparkContext
{
    public IRavenQueryable<Person> People => Session.Query<Person>();
    public IRavenQueryable<Artist> Artists => Session.Query<Artist>();
    public IRavenQueryable<Song> Songs => Session.Query<Song>();
    public IRavenQueryable<MediumType> MediumTypes => Session.Query<MediumType>();
    public IRavenQueryable<Tag> Tags => Session.Query<Tag>();
    public IRavenQueryable<TagCategory> TagCategories => Session.Query<TagCategory>();
    public IRavenQueryable<Playlist> Playlists => Session.Query<Playlist>();
}
