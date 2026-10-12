namespace MintPlayer.Spark.Dashboards;

/// <summary>
/// The values one reading of a dashboard collects from its providers, by attribute name. The last
/// <see cref="Set"/> of a name wins.
/// </summary>
public sealed class DashboardValues
{
    private readonly Dictionary<string, object?> values = new(StringComparer.Ordinal);

    internal DashboardValues(string dashboard, bool isSampling)
    {
        Dashboard = dashboard;
        IsSampling = isSampling;
    }

    /// <summary>The dashboard's model type name (e.g. <c>OperatorDashboard</c>).</summary>
    public string Dashboard { get; }

    /// <summary>
    /// True when the history sampler reads, not a viewer: only the values declared with
    /// <see cref="SparkDashboardBuilder.History"/> are kept, so a provider may skip expensive ones (a table).
    /// </summary>
    public bool IsSampling { get; }

    /// <summary>
    /// Sets <paramref name="attribute"/>'s value. A number, string, boolean or date shows as is; a list or dictionary
    /// travels as JSON for a renderer (<c>spark-dashboard-table</c>); <see cref="Meter"/> builds a meter's value.
    /// </summary>
    public DashboardValues Set(string attribute, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attribute);
        values[attribute] = value;
        return this;
    }

    /// <summary>The value set for <paramref name="attribute"/>, or null.</summary>
    public object? Get(string attribute) => values.GetValueOrDefault(attribute);

    /// <summary>Every value set so far.</summary>
    public IReadOnlyDictionary<string, object?> All => values;

    /// <summary>
    /// A <c>spark-meter</c> value: <paramref name="value"/> of <paramref name="max"/> (e.g. used of total bytes).
    /// Travels as <c>{ "value": …, "max": … }</c>; its history samples <paramref name="value"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> Meter(double value, double? max)
        => new Dictionary<string, object?> { ["value"] = value, ["max"] = max };

    /// <summary>
    /// The one number a value contributes to its time series: a number, or a meter's <c>value</c>. Anything else
    /// (text, a table, a meter without a value) has none: a series is single-valued (D29), so it is never written.
    /// </summary>
    internal static double? SampleOf(object? value) => value switch
    {
        null => null,
        double d => double.IsFinite(d) ? d : null,
        float f => float.IsFinite(f) ? f : null,
        int or long or short or byte or uint or ulong or ushort or sbyte => Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture),
        decimal m => (double)m,
        IReadOnlyDictionary<string, object?> meter when meter.TryGetValue("value", out var inner) && inner is not IReadOnlyDictionary<string, object?> => SampleOf(inner),
        _ => null,
    };
}
