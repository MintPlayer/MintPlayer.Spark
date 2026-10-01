namespace MintPlayer.Spark.Contributions;

/// <summary>
/// Marks a property of a contribution element as one of its slots. A row's slot values identify it:
/// each user has at most one contribution per slot combination, and the target shows one current
/// version per slot combination.
/// </summary>
/// <remarks>
/// <para>
/// Slots are taken in declaration order, which is also their order in the document ids
/// (<c>{targetId}/{Name}/{slot1}/{slot2}</c>).
/// </para>
/// <para>
/// A slot needs one stable, culture-independent text form: <c>string</c>, an <c>enum</c> (by name),
/// an integral type, <c>Guid</c> or <c>bool</c>, or their nullable forms. At run time every formatted
/// value must match <c>^[A-Za-z0-9-]{1,32}$</c>.
/// </para>
/// <para>Inert when no <see cref="ContributionAttribute"/> property points at the type.</para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class ContributionSlotAttribute : Attribute
{
}
