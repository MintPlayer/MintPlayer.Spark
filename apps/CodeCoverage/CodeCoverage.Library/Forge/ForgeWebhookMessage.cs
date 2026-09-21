using MintPlayer.Spark.Messaging.Abstractions;

namespace CodeCoverage.Forge;

/// <summary>
/// One normalised forge event on the bus (D21).
/// </summary>
/// <typeparam name="TEvent">One of the records in <c>ForgeEvents.cs</c>.</typeparam>
/// <param name="Provider">Which forge it came from, for the handlers that care. Most do not.</param>
/// <param name="Event">The event, already in our vocabulary.</param>
/// <param name="RawJson">
/// The forge's original payload, as an escape hatch. ⚠️ A handler that reaches for this has become
/// forge-specific again and should say so, rather than quietly depending on a field only one forge
/// populates.
/// </param>
/// <param name="DeliveryId">
/// The forge's delivery identifier, where it has one, for correlating logs.
/// ⚠️ <b>Not a dedup key.</b> GitLab sends no delivery id at all, and Bitbucket's
/// <c>X-Hook-UUID</c> identifies the <em>hook</em> rather than the delivery — using it to
/// deduplicate would make every Bitbucket delivery after the first look like a repeat and be
/// silently swallowed. Idempotency belongs to the handler.
/// </param>
/// <remarks>
/// <para>
/// <b>Why one type rather than one per forge.</b> With a message type per forge, a codebase with
/// <b>M</b> recipients and <b>N</b> forges needs <b>M×N</b> handler implementations, and every new
/// forge edits <b>M</b> existing consumer classes. With this, it needs <b>M</b> — and N is absorbed
/// once, inside the forge libraries, where a new forge is a new library rather than an edit to every
/// consumer. That is the entire argument, and it is also the test for anything added here: if a
/// change makes consumer code grow with the number of forges, it is the wrong change.
/// </para>
/// <para>
/// <b>Dispatch is exact-type, which is why this works.</b> The bus resolves
/// <c>IRecipient&lt;T&gt;</c> over the stored CLR type exactly, so a base record could never be a
/// dispatch target and an <c>IRecipient&lt;BaseEnvelope&gt;</c> would silently receive nothing.
/// There is no derivation here: all three forge libraries publish <em>this</em> type, with the forge
/// as a field rather than a type distinction. One type, one registration, exact match.
/// </para>
/// <para>
/// ⚠️ <b>The queue name is pinned, and must stay pinned.</b> Without the attribute the name derives
/// from the CLR type, and for a constructed generic that embeds the type argument's
/// <em>assembly-qualified</em> name — so every closed generic becomes its own queue and the name
/// changes whenever the argument's assembly version does. One real database accumulated seven
/// <c>SparkMessaging-*</c> definitions, six of them orphans of exactly this shape, including
/// separate <c>Version=2.0.0.0</c> and <c>Version=3.0.0.0</c> variants of one event.
/// <c>[MessageQueue]</c> is declared <c>Inherited = false</c>, so a base type's attribute would not
/// help even if there were one.
/// </para>
/// <para>
/// A second benefit of closing over <em>our</em> event types: the stored message type name no longer
/// embeds a third-party assembly version, so an in-flight message cannot be dead-lettered by
/// upgrading Octokit.
/// </para>
/// <para>
/// One queue means one FIFO lane, so handlers are serialised with each other. A forge that wants its
/// own lane gets its own queue name on its own envelope — three lanes mean a slow GitHub handler
/// cannot stall a Bitbucket event, and cost no extra RavenDB subscriptions under the default
/// single-subscription mode.
/// </para>
/// </remarks>
[MessageQueue("spark-forge-all")]
public sealed record ForgeWebhookMessage<TEvent>(
    EForgeProvider Provider,
    TEvent Event,
    string? RawJson = null,
    string? DeliveryId = null)
    where TEvent : class;
