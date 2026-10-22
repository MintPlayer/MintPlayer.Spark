using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Entities;

/// <summary>A song, with its credited artists, lyrics and the karaoke timings of those lyrics.</summary>
public partial class Song : Subject
{
    public string Title { get; set; } = string.Empty;
    public DateOnly? Released { get; set; }
    public List<SongArtist> Artists { get; set; } = [];

    /// <summary>The lyrics, one line per row. <c>null</c> until someone adds them.</summary>
    public string? Lyrics { get; set; }

    /// <summary>
    /// Per-recording karaoke timing of <see cref="Lyrics"/>. Written by the timeline sync editor, which needs
    /// playback, so the attribute is hidden on the generic edit page.
    /// </summary>
    public List<LyricsTiming> LyricsTimings { get; set; } = [];
}

/// <summary>An artist credited on a song (legacy <c>ArtistSong</c>).</summary>
[ValueObject]
public partial class SongArtist
{
    [Reference(typeof(Artist))]
    public string? ArtistId { get; set; }

    /// <summary>Whether the artist is officially credited, rather than featured.</summary>
    public bool Credited { get; set; }
}

/// <summary>
/// Karaoke timing for one recording, keyed by <see cref="MediumUrl"/>: the URL the player reports as playing.
/// Entry <c>i</c> of <see cref="StartTimes"/> is when line <c>i</c> of the lyrics starts, in seconds;
/// <c>null</c> means that line is not timed.
/// </summary>
[ValueObject]
public partial class LyricsTiming
{
    public string MediumUrl { get; set; } = string.Empty;
    public List<double?> StartTimes { get; set; } = [];
}
