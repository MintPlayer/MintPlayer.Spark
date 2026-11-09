using MintPlayer.Spark.Abstractions.Authorization;
using DemoApp.Library.Entities;
using MintPlayer.Spark.Actions;

namespace DemoApp.Actions;

public partial class CompanyActions : DefaultPersistentObjectActions<Company>, ISparkOwnsRowSecurity
{
    /// <inheritdoc />
    public string RowSecurityRationale =>
        "Demo data, published in full on purpose — see PersonActions. Companies here are sample records with no owner and no per-caller meaning.";
}
