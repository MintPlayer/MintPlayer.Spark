using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Dashboards.Services;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Dashboards;

/// <summary>
/// The actions class of a dashboard's virtual type (D12). Spark finds a virtual type's actions by name, so each
/// dashboard declares one line:
/// <code>public sealed class OperatorDashboardActions(IServiceProvider services) : SparkDashboardActions(services);</code>
/// The dashboard's name is the class name without <c>Actions</c>, which is also how Spark matched it to the type.
/// </summary>
/// <remarks>
/// <para>
/// The load runs after Spark's own <c>Read</c> check on the type, so a caller without the right never reaches a
/// provider: the load answers 404, and a live subscription ends with the one "gone" answer for unknown, forbidden and
/// deleted alike.
/// </para>
/// <para>
/// The page is read-only, answers only its <see cref="SparkDashboardBuilder.ObjectId"/>, takes its title from the
/// type's label, and carries <c>metadata.dashboard</c> (<c>generatedAtUtc</c>), which is how
/// <c>@mintplayer/ng-spark/dashboards</c> recognises it and draws the tiles.
/// </para>
/// </remarks>
public abstract class SparkDashboardActions(IServiceProvider services)
{
    /// <summary>The key of the page's <see cref="PersistentObject.Metadata"/> entry.</summary>
    public const string MetadataKey = "dashboard";

    private const string Suffix = "Actions";

    /// <summary>The dashboard's declared name: this class's name without <c>Actions</c>, the name its model file declares (an application rename changes neither, D28).</summary>
    protected string DashboardName
    {
        get
        {
            var name = GetType().Name;
            return name.EndsWith(Suffix, StringComparison.Ordinal) ? name[..^Suffix.Length] : name;
        }
    }

    /// <summary>The load hook Spark calls for the virtual type (the duck-typed load contract).</summary>
    public virtual async Task<PersistentObject?> OnLoadAsync(string id, PersistentObject? parent)
    {
        // No AddDashboards (the package referenced, its model layer present) or no declaration: the type has no page.
        if (services.GetService<DashboardCatalog>()?.Find(DashboardName) is not { } dashboard
            || !string.Equals(id, dashboard.ObjectId, StringComparison.Ordinal))
            return null;

        // The model type by identity, not by the name the application gives it (D28).
        if (dashboard.TypeOf(services.GetRequiredService<IModelLoader>()) is not { } type)
            return null;

        var obj = await services.GetRequiredService<IManager>().GetPersistentObjectAsync(type.Id);
        var snapshot = await services.GetRequiredService<DashboardReader>().ReadAsync(dashboard, CancellationToken.None);
        foreach (var (name, value) in snapshot.Values)
        {
            // A value the model does not declare (or the caller may not read) has no attribute: skipped.
            if (obj.TryGetAttribute(name, out var attribute))
                attribute.SetOriginalValue(value);
        }

        obj.Id = id;
        obj.Breadcrumb ??= Title(type);
        obj.Can = new PersistentObjectPermissions { Edit = false, Delete = false };
        obj.Metadata ??= new Dictionary<string, object?>();
        obj.Metadata[MetadataKey] = new Dictionary<string, object?> { ["generatedAtUtc"] = snapshot.GeneratedAtUtc };
        return obj;
    }

    /// <summary>The type's label in the request's language, else its name.</summary>
    private string Title(EntityTypeDefinition definition)
    {
        var culture = services.GetService<IRequestCultureResolver>()?.GetCurrentCulture();
        return definition.Label is { } label && culture is not null && label.GetValue(culture) is { Length: > 0 } text
            ? text
            : definition.Name;
    }
}
