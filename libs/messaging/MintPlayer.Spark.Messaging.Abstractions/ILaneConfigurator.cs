namespace MintPlayer.Spark.Messaging.Abstractions;

/// <summary>
/// Declares one or more lanes. Resolved from the container, so it may inject anything.
/// </summary>
/// <remarks>
/// <para>
/// Lane configuration used to run inside <c>AddMessaging</c>, before the container existed, which
/// meant a lane could not be configured from anything the application had registered — a retry ladder
/// held in options, a concurrency limit derived from a resource probe, a partition key that needed a
/// service. It also forced framework packages to reach into <see cref="IServiceCollection"/> looking
/// for an already-constructed registry, which made the whole thing quietly dependent on the order the
/// <c>Add…</c> calls happened to run in.
/// </para>
/// <para>
/// A configurator is a <b>singleton</b> and is invoked once, lazily, the first time lanes are needed.
/// That is deliberate: what a lane declares — its name, mode, concurrency and schedule — is
/// process-wide data, so building it late is useful but keeping it short-lived is not. A configurator
/// that wants per-request state has misunderstood the seam: <i>handlers</i> are scoped and resolved
/// per message, and that is where request-shaped dependencies belong.
/// </para>
/// </remarks>
public interface ILaneConfigurator
{
    void Configure(ILaneBuilder lanes);
}
