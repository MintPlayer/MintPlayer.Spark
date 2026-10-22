using MintPlayer.Entities;
using MintPlayer.Spark;
using Raven.Client.Documents.Indexes;

namespace MintPlayer.Indexes;

/// <summary>
/// Full-text search over the whole catalog: artists, people and songs mapped into one <see cref="VSubject"/> shape,
/// so one query (and one autocomplete) spans all three. The RavenDB replacement for the legacy Elasticsearch index.
/// Soft-deleted rows are left out at index time, because the search endpoint queries the index directly.
/// </summary>
/// <remarks>
/// Not a default index: it serves no persistent object's list, only the search endpoint. <c>Text</c> is indexed
/// for full-text search and has suggestions, so the endpoint can use both <c>.Search()</c> and
/// <c>.SuggestUsing()</c>.
/// </remarks>
public class Subjects_Search : SparkMultiMapIndexCreationTask<VSubject>
{
    public Subjects_Search()
    {
        AddMap<Artist>(artists => from artist in artists
                                  where artist.IsDeleted != true
                                  select new VSubject
                                  {
                                      Id = artist.Id,
                                      SubjectType = "Artist",
                                      Text = artist.Name,
                                      TagIds = artist.TagIds,
                                  });
        AddMap<Person>(people => from person in people
                                 where person.IsDeleted != true
                                 select new VSubject
                                 {
                                     Id = person.Id,
                                     SubjectType = "Person",
                                     Text = person.FirstName + " " + person.LastName,
                                     TagIds = person.TagIds,
                                 });
        AddMap<Song>(songs => from song in songs
                              where song.IsDeleted != true
                              select new VSubject
                              {
                                  Id = song.Id,
                                  SubjectType = "Song",
                                  Text = song.Title,
                                  TagIds = song.TagIds,
                              });

        Index(x => x.Text, FieldIndexing.Search);
        Suggestion(x => x.Text);
        StoreAllFields(FieldStorage.Yes);
    }
}

/// <summary>One search hit from <see cref="Subjects_Search"/>.</summary>
public class VSubject
{
    public string? Id { get; set; }
    /// <summary>The collection the hit came from: <c>"Artist"</c>, <c>"Person"</c> or <c>"Song"</c>.</summary>
    public string SubjectType { get; set; } = string.Empty;
    /// <summary>The display and search text: the artist's name, the person's full name or the song's title.</summary>
    public string Text { get; set; } = string.Empty;
    public List<string> TagIds { get; set; } = [];
}
