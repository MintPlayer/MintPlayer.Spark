using System.Reflection;

namespace MintPlayer.Spark.Services;

/// <summary>
/// How Spark calls application-authored code through reflection.
/// </summary>
/// <remarks>
/// <para>
/// Every hook, actions method, query source and message recipient is invoked with
/// <see cref="BindingFlags.DoNotWrapExceptions"/>. Without it, <see cref="MethodBase.Invoke"/> wraps
/// anything the target throws in a <see cref="TargetInvocationException"/> — and then <b>no typed
/// <c>catch</c> anywhere in the framework matches it</b>. A hook refusing politely with
/// <c>SparkValidationException</c> reaches the caller as a 500 with its message lost; one raising
/// <c>SparkRetryActionException</c> never becomes a 449.
/// </para>
/// <para>
/// ⚠️ <b>Why this was missed on eleven call sites and found on a twelfth.</b> The wrapping only
/// happens when the target throws <i>before</i> its <see cref="Task"/> exists. An <c>async</c>
/// override's exception lands on the returned task and <c>await</c> rethrows it unwrapped, so the
/// ordinary case looks correct. A <b>non-async</b> override that validates and refuses up front does
/// not — and that is the shape of a guard clause, which makes it the shape most likely to be written.
/// </para>
/// <para>
/// Named rather than spelled out at each site so the rule is one thing with one explanation, and so
/// <c>HookInvocationTests</c> can state it as an invariant: any reflective call into an actions
/// instance must pass this.
/// </para>
/// </remarks>
internal static class SparkHookInvocation
{
    /// <inheritdoc cref="SparkHookInvocation" />
    public const BindingFlags HookInvoke = BindingFlags.DoNotWrapExceptions;
}
