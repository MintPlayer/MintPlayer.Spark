using System.Text.RegularExpressions;
using MintPlayer.Entities;
using MintPlayer.Spark;
using MintPlayer.YouTube;

namespace MintPlayer.Indexes;

/// <summary>
/// Songs by the canonical YouTube video id of each of their media links: the lookup behind the YouTube playlist
/// import. The catalog stores many URL variants, so the map normalises them with the importer's own
/// <see cref="YouTubeUrl.VideoIdPattern"/>, and a playlist of N videos is matched with one <c>YouTubeIds in (…)</c>
/// query. Soft-deleted songs are not indexed.
/// </summary>
public class Songs_ByYouTubeId : SparkIndexCreationTask<Song>
{
    public class Result
    {
        public string[] YouTubeIds { get; set; } = [];
    }

    public Songs_ByYouTubeId()
    {
        Map = songs => from song in songs
                       where song.IsDeleted != true
                       select new Result
                       {
                           YouTubeIds = song.Media
                               .Where(m => m.Value != null)
                               .Select(m => Regex.Match(m.Value, YouTubeUrl.VideoIdPattern))
                               .Where(match => match.Success)
                               .Select(match => match.Groups[1].Value)
                               .ToArray(),
                       };
    }
}
