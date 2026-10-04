namespace MintPlayer.Spark.Abstractions.Authorization;

/// <summary>
/// Declares the <c>security.json</c> action verbs an assembly asks for, by pointing at a static class
/// of <c>public const string</c> fields (#460 contributions, Q2):
/// <code>[assembly: SparkReservedActions(typeof(SoftDeleteRights))]</code>
/// </summary>
/// <remarks>
/// <para>
/// A reserved verb belongs to the framework or a package: a custom action named like one would share
/// its right (<c>Restore/Car</c> would authorize both), so it is refused — at build time by the
/// SPARK023 analyzer, which reads the constants from the compilation and every referenced assembly,
/// and at startup by <c>UseSpark()</c>, which reads them by reflection. The same declarations are the
/// analyzer's list of actions Spark asks for (SPARK011), so a package's verbs are known wherever the
/// package is referenced and nowhere else.
/// </para>
/// <para>
/// Only <c>public const string</c> fields count. A constant that is not a verb (a target name, say)
/// is excluded with <see cref="SparkNotAnActionAttribute"/>.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
public sealed class SparkReservedActionsAttribute : Attribute
{
    /// <param name="verbs">A static class whose <c>public const string</c> fields are the verbs.</param>
    public SparkReservedActionsAttribute(Type verbs)
    {
        Verbs = verbs;
    }

    /// <summary>The class declaring the verbs.</summary>
    public Type Verbs { get; }
}

/// <summary>
/// Marks a <c>const string</c> on a <see cref="SparkReservedActionsAttribute"/> class that is not an
/// action verb, such as the name of a pseudo-target.
/// </summary>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class SparkNotAnActionAttribute : Attribute
{
}
