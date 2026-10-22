using MintPlayer.Entities;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Actions;

namespace MintPlayer.Actions;

public partial class SongArtistActions : DefaultPersistentObjectActions<SongArtist>, ISparkOwnsRowSecurity
{
    public string RowSecurityRationale => CatalogRowSecurity.Rationale;
}
