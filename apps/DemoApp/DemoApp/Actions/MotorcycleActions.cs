using DemoApp.Library.Entities;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Actions;

namespace DemoApp.Actions;

/// <summary>
/// security.json grants Motorcycle to anonymous visitors, and startup refuses an anonymous grant whose row rule is
/// undecided. The demo publishes every row, like the rest of DemoApp's types.
/// </summary>
public partial class MotorcycleActions : DefaultPersistentObjectActions<Motorcycle>, ISparkOwnsRowSecurity
{
    /// <inheritdoc />
    public string RowSecurityRationale =>
        "Demo data, published in full on purpose — see PersonActions. Motorcycle exists to show a multi-map default index (TwoWheelers_Overview).";
}
