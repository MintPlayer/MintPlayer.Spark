using MintPlayer.Entities;
using MintPlayer.Spark;
using Raven.Client.Documents.Linq;

namespace MintPlayer;

/// <summary>The catalog (people, artists, songs, their tags and medium types) and the users' playlists.</summary>
public partial class MintPlayerContext : SparkContext
{
    public IQueryable<Person> People => Session.Query<Person>();
    public IQueryable<Artist> Artists => Session.Query<Artist>();
    public IQueryable<Song> Songs => Session.Query<Song>();
    public IQueryable<MediumType> MediumTypes => Session.Query<MediumType>();
    public IQueryable<Tag> Tags => Session.Query<Tag>();
    public IQueryable<TagCategory> TagCategories => Session.Query<TagCategory>();
    public IQueryable<Playlist> Playlists => Session.Query<Playlist>();
}
