using MintPlayer.Entities;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Actions;

namespace MintPlayer.Actions;

public partial class MediumActions : DefaultPersistentObjectActions<Medium>, ISparkOwnsRowSecurity
{
    public string RowSecurityRationale => CatalogRowSecurity.Rationale;
}
