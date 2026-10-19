namespace MintPlayer.Spark.Replication.Abstractions.Configuration;

/// <summary>
/// Configuration options for Spark module replication, bound from the <c>Spark:Replication</c>
/// configuration section and then overridable in code.
/// </summary>
/// <remarks>
/// <b>Never give a collection property a non-empty initializer here.</b> The rule is about
/// <i>collections</i>, not initializers in general. Scalars are replaced by a configured value, so
/// initialize them freely. Collections are <b>appended to</b>: the configured values land <i>after</i>
/// whatever the initializer put there, so the hardcoded default ends up <b>first</b> — and first is
/// the position that decides behaviour (<c>DocumentStore</c> connects to <c>Urls[0]</c>).
/// <para>
/// That ordering is what made F14 silent rather than loud: the configured URL <i>was</i> bound, just
/// second, and nothing ever read it.
/// </para>
/// <para>
/// This is where that shipped, as <b>F14</b>. <see cref="SparkModulesUrls"/> initialised to
/// <c>["http://localhost:8080"]</c>; a configured URL landed <i>after</i> it; and
/// <c>DocumentStore</c> connects to the first URL. Every deployment that configured where its module
/// registry lived was still talking to localhost, with no error and a config file that said
/// otherwise. Nothing caught it for days — it surfaced only when two processes could not see each
/// other's data.
/// </para>
/// <para>
/// The pattern to follow instead is <see cref="SparkModulesUrls"/> as it stands now: default the
/// property to empty, put the real default in a separate <c>Default*</c> constant, and expose a
/// <c>Resolved*</c> member that picks one. Consumers read the resolved member; nothing reads the raw
/// property.
/// </para>
/// </remarks>
public class SparkReplicationOptions
{
    /// <summary>Name of this module (e.g. "Fleet", "HR").</summary>
    public required string ModuleName { get; set; }

    /// <summary>The publicly reachable URL of this module (e.g. "https://localhost:5001").</summary>
    public required string ModuleUrl { get; set; }

    /// <summary>
    /// RavenDB URLs for the shared SparkModules database. Defaults to
    /// <see cref="DefaultSparkModulesUrl"/> when left unset — read it through
    /// <see cref="ResolvedSparkModulesUrls"/>, never directly.
    /// </summary>
    /// <remarks>
    /// Empty by default <b>on purpose</b>. .NET's configuration binder does not replace a
    /// collection that already has elements, it appends to it — so a hardcoded initializer here
    /// survived binding and stayed <i>first</i> in the array, which is the URL
    /// <c>DocumentStore</c> actually connects to. An app that configured this setting was still
    /// talking to <c>localhost:8080</c>, with no error and a config file that said otherwise.
    /// </remarks>
    public string[] SparkModulesUrls { get; set; } = [];

    /// <summary>Where SparkModules lives when nothing is configured.</summary>
    public const string DefaultSparkModulesUrl = "http://localhost:8080";

    /// <summary>
    /// The URLs to actually connect to: what was configured, or the default when nothing was.
    /// Applying the default here rather than in the property initializer is what keeps a
    /// configured value from queueing up behind it.
    /// </summary>
    public string[] ResolvedSparkModulesUrls =>
        SparkModulesUrls.Length > 0 ? SparkModulesUrls : [DefaultSparkModulesUrl];

    /// <summary>Name of the shared SparkModules database where all modules register.</summary>
    public string SparkModulesDatabase { get; set; } = "SparkModules";

    /// <summary>
    /// How often <c>SyncActionRetrySweeper</c> wakes sync actions whose retry backoff has elapsed.
    /// This is the granularity of retry redelivery, not of first delivery — a new action is
    /// delivered immediately by the subscription and never waits for a sweep.
    /// </summary>
    public TimeSpan FallbackPollInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Assemblies to scan for <c>[Replica]</c> types. If empty, scans the entry assembly.</summary>
    public System.Reflection.Assembly[] AssembliesToScan { get; set; } = [];

    /// <summary>
    /// How this module, as an owner, delivers its contracts to consumers (warehouse WD1, WX1):
    /// <see cref="ReplicationTransport.Auto"/> (default) reads the RavenDB licence at startup and uses
    /// ETL when it has it, HTTP otherwise. Tests and demos pin <see cref="ReplicationTransport.Http"/>,
    /// because local runs and CI hold the Developer licence, which has ETL (Q-W24).
    /// </summary>
    public ReplicationTransport Transport { get; set; } = ReplicationTransport.Auto;

    /// <summary>
    /// The contracts this module reads and where from, keyed by contract name:
    /// <c>Spark:Replication:Consume:{contract}:Sources</c>. A replica type whose contract has no
    /// sources here receives nothing, and startup says so.
    /// </summary>
    public Dictionary<string, ReplicationConsumeOptions> Consume { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The <c>SparkModules</c> registry: who may write it (WX4).</summary>
    public ReplicationRegistryOptions Registry { get; set; } = new();

    /// <summary>Delivery of pushes on the HTTP transport (owner side).</summary>
    public ReplicationPushOptions Push { get; set; } = new();

    /// <summary>The digest reconcile on the HTTP transport (consumer side).</summary>
    public ReplicationReconcileOptions Reconcile { get; set; } = new();

    /// <summary>
    /// Published contracts that change on nearly every write (stock levels): instead of one push per save, a save marks
    /// the document dirty in its own commit, and a flush every <see cref="ReplicationPushOptions.DirtySetFlushSchedule"/>
    /// pushes the latest state of every dirty document once (warehouse §4.2, §4.4). The messaging outbox cannot
    /// deduplicate (it refuses a <c>DeduplicationKey</c>), the dirty set can.
    /// </summary>
    public HashSet<string> DirtySetContracts { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Delivery of write-back requests to an owner (consumer side; F14).</summary>
    public ReplicationWriteBackOptions WriteBack { get; set; } = new();

    private readonly List<ReplicationContract> contracts = [];

    /// <summary>
    /// The contracts this module publishes as an owner, in declaration order. Filled by
    /// <c>Publish</c>; code only, never bound from configuration.
    /// </summary>
    public IReadOnlyList<ReplicationContract> Contracts => contracts;

    /// <summary>
    /// Publishes a replication contract: consumers whose <c>[Replica]</c> type reads this name and
    /// version receive <typeparamref name="TContract"/> records projected from this module's
    /// <typeparamref name="TSource"/> documents.
    /// <para>
    /// On the HTTP transport the projection is compiled and may compute (Q-W17). The ETL transport
    /// needs a plain field selection with optional renames:
    /// <c>s =&gt; new CarV1 { Plate = s.LicensePlate, Street = s.Address.Street }</c>, or a
    /// positional record constructor whose arguments are member paths, from which the owner generates
    /// the transform. A projection that needs code is HTTP-only: startup says so, and refuses when the
    /// transport is pinned to <see cref="ReplicationTransport.Etl"/>.
    /// </para>
    /// <para>
    /// Write-back maps a replica field back to the source property it was selected from; a field that
    /// is computed or read from a nested path cannot be written back.
    /// </para>
    /// </summary>
    /// <param name="contract">Permanent lower-kebab name, e.g. <c>cars</c>. Never rename it once published.</param>
    /// <param name="version">The contract version, 1 or higher. Versions run side by side.</param>
    /// <param name="projection">The projection from the source document to the contract record.</param>
    /// <exception cref="ArgumentException">The name is not lower-kebab, the version is below 1, or the pair is already published.</exception>
    public SparkReplicationOptions Publish<TSource, TContract>(
        string contract,
        int version,
        System.Linq.Expressions.Expression<Func<TSource, TContract>> projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        if (!ReplicationContract.IsValidName(contract))
            throw new ArgumentException(
                $"Replication contract name '{contract}' must be lower-kebab (a-z, digits, single hyphens), e.g. 'cars' or 'fulfilment-orders'.",
                nameof(contract));
        if (version < 1)
            throw new ArgumentException($"Replication contract '{contract}' version must be 1 or higher, not {version}.", nameof(version));
        if (contracts.Any(c => c.Name == contract && c.Version == version))
            throw new ArgumentException($"Replication contract '{contract}' v{version} is published twice.", nameof(contract));

        contracts.Add(new ReplicationContract(contract, version, typeof(TSource), typeof(TContract), projection));
        return this;
    }

    /// <summary>
    /// Publishes <typeparamref name="TContract"/> under the name and version its
    /// <see cref="ReplicationContractAttribute"/> states — the form to use with a shared contracts package.
    /// </summary>
    /// <exception cref="ArgumentException"><typeparamref name="TContract"/> carries no <see cref="ReplicationContractAttribute"/>.</exception>
    public SparkReplicationOptions Publish<TSource, TContract>(
        System.Linq.Expressions.Expression<Func<TSource, TContract>> projection)
    {
        var declared = ReplicationContractAttribute.Of(typeof(TContract))
            ?? throw new ArgumentException(
                $"{typeof(TContract).FullName} has no [ReplicationContract(name, version)]; name the contract explicitly or declare it on the record.",
                nameof(projection));
        return Publish(declared.Name, declared.Version, projection);
    }

    /// <summary>
    /// Cross-module mTLS configuration (R2-C1 / R2-C2 / R2-H7). Each module deploys
    /// with its own X.509 client certificate; cross-module requests carry the cert and
    /// inbound endpoints verify the cert thumbprint matches the one pinned for the
    /// requesting module in SparkModules. On first registration the module pins its
    /// own thumbprint; subsequent re-registrations from a different cert thumbprint
    /// are refused, closing the trust-on-first-claim hole.
    /// </summary>
    public SparkReplicationCertificateOptions ClientCertificate { get; set; } = new();
}

/// <summary>
/// Mode that drives cross-module mTLS enforcement.
/// </summary>
public enum SparkReplicationCertificateMode
{
    /// <summary>
    /// Auto-detect: <see cref="Production"/> when the host environment is anything
    /// other than <c>Development</c>; <see cref="Development"/> when it is. The
    /// default — keeps prod safe-by-default without requiring every appsettings
    /// file to opt into mTLS.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Production: inbound endpoints require a valid client cert whose SHA-256
    /// thumbprint matches the requesting module's pinned thumbprint; outbound
    /// HttpClients attach the configured per-target cert.
    /// </summary>
    Production = 1,

    /// <summary>
    /// Development: inbound endpoints accept calls without a client cert, BUT
    /// they still verify that the <c>RequestingModule</c> is registered in
    /// SparkModules and log a warning per accepted call so the relaxed mode is
    /// visible. Outbound HttpClients send the cert if configured, omit it
    /// otherwise. Use only in dev/test where every module is on a trusted
    /// network.
    /// </summary>
    Development = 2,

    /// <summary>
    /// Disabled: passthrough — no cert validation, no warning log. Reserved for
    /// the legacy demo path; new apps should pick Production or Development.
    /// </summary>
    Disabled = 3,
}

/// <summary>
/// mTLS settings for cross-module replication endpoints.
/// <para>
/// The certificate is THIS module's identity — the same cert is presented on
/// every outbound call regardless of target. Receivers validate the cert's
/// SHA-256 thumbprint against the pinned value stored when this module
/// registered in <c>SparkModules</c>. Per-target overrides exist as an advanced
/// escape hatch only (e.g. different CAs per peer); the primary config is a
/// single cert per module.
/// </para>
/// <para>
/// See <c>docs/guide-replication-mtls.md</c> for the full operator walkthrough.
/// </para>
/// </summary>
public class SparkReplicationCertificateOptions
{
    /// <summary>
    /// Enforcement mode. Default <see cref="SparkReplicationCertificateMode.Auto"/>
    /// resolves to <c>Production</c> in non-Development host environments.
    /// </summary>
    public SparkReplicationCertificateMode Mode { get; set; } = SparkReplicationCertificateMode.Auto;

    /// <summary>
    /// SHA-256 thumbprint of THIS module's client certificate (uppercase hex). Used
    /// during registration to pin the thumbprint in SparkModules so other modules can
    /// validate inbound calls from this one. Sourced from operator config (appsettings,
    /// env, key vault).
    /// </summary>
    public string? Thumbprint { get; set; }

    /// <summary>
    /// Path to the PKCS#12 (.pfx) file containing THIS module's client certificate and its
    /// private key — mTLS needs the key, so a bare PEM certificate cannot stand in. Attached
    /// to every outbound replication HttpClient by default. Leave null in
    /// <see cref="SparkReplicationCertificateMode.Development"/> when running on a
    /// trusted network.
    /// </summary>
    public string? CertificateFile { get; set; }

    /// <summary>Optional password for <see cref="CertificateFile"/>.</summary>
    public string? CertificatePassword { get; set; }

    /// <summary>
    /// Advanced escape hatch: per-target overrides for the outbound cert. Key =
    /// target module name. Value = cert file + optional password. Used when
    /// different peer modules trust different CAs — uncommon. Falls through to
    /// <see cref="CertificateFile"/> when the target isn't in this dictionary.
    /// </summary>
    public Dictionary<string, SparkOutboundCertificate> PerTargetOverrides { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// One outbound certificate entry — used in <see cref="SparkReplicationCertificateOptions.PerTargetOverrides"/>.
/// </summary>
public class SparkOutboundCertificate
{
    /// <summary>Path to the PKCS#12 (.pfx) file presented when calling the target module.</summary>
    public required string CertificateFile { get; set; }

    /// <summary>Optional password for <see cref="CertificateFile"/>.</summary>
    public string? CertificatePassword { get; set; }
}
