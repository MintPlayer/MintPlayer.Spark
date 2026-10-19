using MintPlayer.ValueComparerGenerator.Attributes;

namespace MintPlayer.Spark.SourceGenerators.Models;

/// <summary>The kinds <c>SparkDiscoveryGenerator</c> registers, in the order the generated method lists them.</summary>
public enum DiscoveryKind
{
    Actions,
    CustomAction,
    Recipient,
    CronJob,
    Migration,
    Interceptor,
    SubscriptionWorker,
    RowPolicy,
    Discoverable,
}

/// <summary>One registration line found by the shared discovery pass (X20 item 3).</summary>
[GenerateEquality]
public partial class DiscoveredRegistration
{
    public DiscoveryKind Kind { get; set; }

    /// <summary>The discovered class, fully qualified (<c>global::…</c>).</summary>
    public string TypeName { get; set; } = string.Empty;

    /// <summary>
    /// The second type the registration names: the entity (Actions), the message (Recipient), or the
    /// service interface (Discoverable). Empty for the other kinds.
    /// </summary>
    public string ServiceTypeName { get; set; } = string.Empty;

    /// <summary>Recipient only: the class also implements <c>ICheckpointRecipient&lt;TMessage&gt;</c> for this message.</summary>
    public bool IsCheckpointRecipient { get; set; }

    /// <summary>Discoverable only: the <c>ServiceLifetime</c> value (0 Singleton, 1 Scoped, 2 Transient).</summary>
    public int Lifetime { get; set; } = 1;
}

/// <summary>Which kinds the compilation can register: each kind is emitted only when its key types resolve.</summary>
[GenerateEquality]
public partial class DiscoveryGates
{
    /// <summary><c>ISparkBuilder</c> resolves: the generated method's own signature needs it.</summary>
    public bool HasBuilder { get; set; }
    public bool Actions { get; set; }
    public bool CustomActions { get; set; }
    public bool Recipients { get; set; }
    public bool CronJobs { get; set; }
    public bool Migrations { get; set; }
    public bool Interceptors { get; set; }
    public bool SubscriptionWorkers { get; set; }
    public bool RowPolicies { get; set; }
    public bool Discoverable { get; set; }

    /// <summary>
    /// <c>MintPlayer.Spark.AllFeatures.SparkFullOptions</c> resolves. The generated <c>AddSparkFull</c>
    /// always calls <c>AddSparkDiscovered</c>, so the method must exist even when nothing was discovered.
    /// </summary>
    public bool AllFeatures { get; set; }
}
