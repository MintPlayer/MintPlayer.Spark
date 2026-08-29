using DemoApp.Library.Entities;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Actions;

namespace DemoApp.CustomActions;

/// <summary>
/// Duplicates the selected cars, and — when invoked from a company's <c>company-cars</c> sub-query —
/// assigns the copies to <b>that company</b>.
/// </summary>
/// <remarks>
/// <para>
/// The sample for the sub-query selection flow: pick rows in a grid rendered on another object's
/// detail page, click an action, and have the action know both <em>which rows</em> and <em>which
/// page</em>. Those are two different questions, and they arrive as two different things.
/// </para>
/// <list type="bullet">
/// <item><description><see cref="CustomActionArgs.SelectedItems"/> — the rows, re-loaded server-side
/// through the row-gated read path. Never the objects the browser posted: the client sends
/// <c>selectedItemIds</c> and nothing else, because a grid row is a projection, not a document.
/// If any id fails to resolve the whole request is refused rather than acting on the
/// survivors.</description></item>
/// <item><description><see cref="CustomActionArgs.QueryParent"/> — the Company whose page the grid
/// was on, resolved under <em>its own</em> type with its own <c>Read</c> gate. Null on the top-level
/// <c>/query/cars</c> page, which is why the fallback below exists.</description></item>
/// <item><description><see cref="CustomActionArgs.Parent"/> — <b>not</b> used here, and worth saying
/// why: it means "an object of this action's own type", i.e. the car whose detail page you clicked
/// from. On a sub-query it is null, and expecting the Company there is the mistake this separation
/// exists to prevent.</description></item>
/// </list>
/// </remarks>
public partial class CopyCarsToCompanyAction : SparkCustomAction
{
    [Inject] private readonly IDatabaseAccess dbAccess;

    public override async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken)
    {
        // Coalesce on IDS, not on objects. A selected row is a QueryResultItem -- an id, a display
        // string and a value per column -- while Parent is a PersistentObject, so the two no longer
        // unify. That is deliberate: a row is not a document, and this action wants documents.
        string[] carIds = args.SelectedItems.Length > 0
            ? [.. args.SelectedItems.Select(i => i.Id)]
            : args.Parent?.Id is { Length: > 0 } parentId ? [parentId] : [];

        if (carIds.Length == 0)
            throw new InvalidOperationException("Select at least one car to copy.");

        // The company whose page we were on. Absent on the top-level car list, where each copy
        // simply keeps the owner of the car it was copied from.
        var targetCompanyId = args.QueryParent?.Id;

        foreach (var carId in carIds)
        {
            var car = await dbAccess.GetDocumentUncheckedAsync<Car>(carId)
                ?? throw new InvalidOperationException($"Car '{carId}' not found.");

            await dbAccess.SaveDocumentUncheckedAsync(new Car
            {
                LicensePlate = $"{car.LicensePlate} (copy)",
                Model = car.Model,
                Year = car.Year,
                Color = car.Color,
                Brand = car.Brand,
                Status = car.Status,
                Owner = targetCompanyId ?? car.Owner,
            });
        }
    }
}
