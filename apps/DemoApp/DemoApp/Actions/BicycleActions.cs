using DemoApp.Library.Entities;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Actions;

namespace DemoApp.Actions;

/// <summary>
/// security.json grants Bicycle to anonymous visitors, and startup refuses an anonymous grant whose row rule is undecided.
/// The demo publishes every row, like the rest of DemoApp's types.
/// </summary>
public partial class BicycleActions : DefaultPersistentObjectActions<Bicycle>, ISparkOwnsRowSecurity
{
    /// <inheritdoc />
    public string RowSecurityRationale =>
        "Demo data, published in full on purpose — see PersonActions. Bicycle exists to show a multi-map default index (TwoWheelers_Overview).";
}
