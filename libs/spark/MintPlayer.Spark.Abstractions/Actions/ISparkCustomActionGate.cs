using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace MintPlayer.Spark.Abstractions.Actions;

/// <summary>
/// A last check before a custom action runs (<c>docs/seo_analyzer_PRD.md</c> X3): after authentication, the type
/// right, the catalogue, the row gates and the disabled-action gate, so it runs only for a request that would
/// otherwise execute, and a refusal here discloses nothing about rows the caller cannot see. Register one with
/// <c>services.TryAddEnumerable(ServiceDescriptor.Scoped&lt;ISparkCustomActionGate, MyGate&gt;())</c>.
/// </summary>
/// <remarks>
/// Throw <see cref="SparkThrottledException"/> to answer 429 with <c>Retry-After</c> (a quota), or
/// <see cref="SparkValidationException"/> for a 400 with a readable reason. The per-user quotas of the settings
/// package (<c>[SparkQuota]</c>) are one.
/// </remarks>
public interface ISparkCustomActionGate
{
    ValueTask BeforeExecuteAsync(SparkCustomActionGateContext context, CancellationToken cancellationToken);
}

/// <summary>What is about to run.</summary>
public sealed class SparkCustomActionGateContext
{
    /// <summary>The action's name as the catalogue knows it.</summary>
    public required string ActionName { get; init; }

    /// <summary>The <see cref="ICustomAction"/> class that will run (attributes on it configure gates).</summary>
    public required Type ActionType { get; init; }

    /// <summary>The persistent object type the action was invoked on.</summary>
    public required string EntityTypeName { get; init; }

    /// <summary>The caller.</summary>
    public ClaimsPrincipal? User { get; init; }

    /// <summary>The request.</summary>
    public required HttpContext HttpContext { get; init; }
}
