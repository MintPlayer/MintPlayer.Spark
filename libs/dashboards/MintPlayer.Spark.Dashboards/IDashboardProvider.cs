namespace MintPlayer.Spark.Dashboards;

/// <summary>
/// Fills some of a dashboard's attribute values (<c>docs/guide-dashboards.md</c>). Attached to a dashboard with
/// <see cref="SparkDashboardBuilder.Provider{TProvider}"/>; several providers may fill one dashboard.
/// </summary>
/// <remarks>
/// <para>
/// <b>Runs as the system, not as the viewer.</b> One reading is shared by every viewer for
/// <see cref="SparkDashboardsOptions.CacheDuration"/>, and the history sampler runs it with nobody signed in. Only
/// callers holding <c>Read</c> on the dashboard's type see it, so the values must not depend on who asks; read with a
/// raw <c>IDocumentStore</c> session.
/// </para>
/// <para>
/// Created per reading in its own DI scope (<c>ActivatorUtilities</c>), so constructor injection works and a
/// scoped dependency is fresh each time.
/// </para>
/// <para>
/// No mount paths, device names, exception text or environment values in a value: labels are the model's.
/// </para>
/// </remarks>
public interface IDashboardProvider
{
    /// <summary>
    /// Sets values on <paramref name="values"/> by attribute name. A name the dashboard's model does not declare is
    /// ignored when the page is built, so one provider can serve dashboards that show only some of its values.
    /// </summary>
    Task FillAsync(DashboardValues values, CancellationToken cancellationToken);
}
