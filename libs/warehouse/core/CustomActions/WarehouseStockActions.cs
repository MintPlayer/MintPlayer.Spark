using System.Globalization;
using System.Text.Json;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Warehouse.Models;
using MintPlayer.Spark.Warehouse.Stock;

namespace MintPlayer.Spark.Warehouse.CustomActions;

/// <summary>
/// The custom actions the warehouse core ships (PRD §4.5: moves and adjustments are domain services exposed as custom
/// actions; stock is read-only in the UI otherwise). Names carry the library's prefix, because custom-action names are
/// first-wins across assemblies. Rights are in the library's <c>security.json</c>, on its slots.
/// </summary>
public static class WarehouseActionNames
{
    public const string MoveStock = "WarehouseMoveStock";
    public const string AdjustStock = "WarehouseAdjustStock";
    public const string RecallLot = "WarehouseRecallLot";
    public const string QuarantineLot = "WarehouseQuarantineLot";
    public const string ReleaseLot = "WarehouseReleaseLot";
}

/// <summary>
/// A stock command form (<see cref="WarehouseStockCommand"/>): never stored, it carries what the person typed to
/// <see cref="WarehouseMoveStockAction"/> or <see cref="WarehouseAdjustStockAction"/>. The op id is the form's, so a
/// double click or a retried request is applied once (WI4).
/// </summary>
public sealed class WarehouseStockCommand
{
    public string? Id { get; set; }

    /// <summary>The idempotency key (the client mints one per opened form).</summary>
    public string OpId { get; set; } = "";

    /// <summary>A move's source balance.</summary>
    public string? BalanceId { get; set; }

    /// <summary>A move's target, or an adjustment's location.</summary>
    public string? LocationId { get; set; }

    /// <summary>An adjustment's item.</summary>
    public string? ItemId { get; set; }

    public string? LotCode { get; set; }

    public DateOnly? Expiry { get; set; }

    /// <summary>A move's quantity (positive), or an adjustment's signed delta, in base units.</summary>
    public decimal Quantity { get; set; }

    /// <summary>An adjustment's reason code.</summary>
    public string? ReasonCode { get; set; }
}

/// <summary>Moves stock from a balance to a location (PRD §4.5), with the form's op id.</summary>
internal sealed partial class WarehouseMoveStockAction : ICustomAction
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly IWarehouseStock stock;
    [Inject] private readonly ISparkCurrentUser currentUser;

    public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        var form = args.SubmittedParent;
        if (form is null || CommandForm.Text(form, nameof(WarehouseStockCommand.BalanceId)) is not { } balanceId
            || CommandForm.Text(form, nameof(WarehouseStockCommand.LocationId)) is not { } locationId)
        {
            manager.Client.Notify(manager.GetTranslatedMessage("warehouse.commandIncomplete"), NotificationKind.Warning);
            return;
        }

        var result = await stock.MoveAsync(new MoveStock(CommandForm.Op(form, currentUser), balanceId, locationId,
            CommandForm.Number(form, nameof(WarehouseStockCommand.Quantity))), cancellationToken);
        CommandForm.Report(manager, result);
    }
}

/// <summary>Adjusts stock with a reason code (PRD §4.5 "Read-only stock": corrections are adjustments).</summary>
internal sealed partial class WarehouseAdjustStockAction : ICustomAction
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly IWarehouseStock stock;
    [Inject] private readonly ISparkCurrentUser currentUser;

    public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        var form = args.SubmittedParent;
        if (form is null || CommandForm.Text(form, nameof(WarehouseStockCommand.LocationId)) is not { } locationId
            || CommandForm.Text(form, nameof(WarehouseStockCommand.ItemId)) is not { } itemId)
        {
            manager.Client.Notify(manager.GetTranslatedMessage("warehouse.commandIncomplete"), NotificationKind.Warning);
            return;
        }

        var result = await stock.AdjustAsync(new AdjustStock(CommandForm.Op(form, currentUser), locationId, itemId,
            CommandForm.Text(form, nameof(WarehouseStockCommand.LotCode)),
            CommandForm.Number(form, nameof(WarehouseStockCommand.Quantity)),
            CommandForm.Text(form, nameof(WarehouseStockCommand.ReasonCode)) ?? "",
            CommandForm.Date(form, nameof(WarehouseStockCommand.Expiry))), cancellationToken);
        CommandForm.Report(manager, result);
    }
}

/// <summary>Recalls the lot on the page (round 5, commerce GA7): blocked at once; Outbound hears it and re-allocates.</summary>
internal sealed partial class WarehouseRecallLotAction : ICustomAction
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly IWarehouseStock stock;
    [Inject] private readonly ISparkCurrentUser currentUser;

    public Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
        => LotStatusChange.RunAsync(manager, stock, currentUser, args, StockLotStatus.Recalled, cancellationToken);
}

internal sealed partial class WarehouseQuarantineLotAction : ICustomAction
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly IWarehouseStock stock;
    [Inject] private readonly ISparkCurrentUser currentUser;

    public Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
        => LotStatusChange.RunAsync(manager, stock, currentUser, args, StockLotStatus.Quarantine, cancellationToken);
}

internal sealed partial class WarehouseReleaseLotAction : ICustomAction
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly IWarehouseStock stock;
    [Inject] private readonly ISparkCurrentUser currentUser;

    public Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
        => LotStatusChange.RunAsync(manager, stock, currentUser, args, StockLotStatus.Released, cancellationToken);
}

internal static class LotStatusChange
{
    public static async Task RunAsync(IManager manager, IWarehouseStock stock, ISparkCurrentUser currentUser, CustomActionArgs args,
        StockLotStatus status, CancellationToken cancellationToken)
    {
        var ids = args.Parent?.Id is { Length: > 0 } parent
            ? [parent]
            : args.SelectedItems.Select(i => i.Id).OfType<string>().Where(id => id.Length > 0).Distinct().ToList();
        foreach (var lotId in ids)
        {
            // One op id per lot and target status per request: a repeated click within the request is one change.
            var op = new StockOp($"lot-status/{lotId}/{status}/{Guid.NewGuid():N}", currentUser.Id, Reference: lotId);
            CommandForm.Report(manager, await stock.ChangeLotStatusAsync(new ChangeLotStatus(op, lotId, status), cancellationToken));
        }
    }
}

/// <summary>Reads a submitted command form; values arrive as JSON elements or plain values.</summary>
internal static class CommandForm
{
    public static StockOp Op(PersistentObject form, ISparkCurrentUser currentUser)
        => new(Text(form, nameof(WarehouseStockCommand.OpId)) ?? Guid.NewGuid().ToString("N"), currentUser.Id, Reference: "ui");

    public static string? Text(PersistentObject form, string name)
    {
        if (!form.TryGetAttribute(name, out var attribute) || attribute.Value is null)
            return null;
        var text = attribute.Value is JsonElement { ValueKind: JsonValueKind.String } json ? json.GetString() : attribute.Value is JsonElement other ? other.ToString() : attribute.Value.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    public static decimal Number(PersistentObject form, string name)
        => Text(form, name) is { } text && decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : 0;

    public static DateOnly? Date(PersistentObject form, string name)
        => Text(form, name) is { } text && DateOnly.TryParse(text.Length >= 10 ? text[..10] : text, CultureInfo.InvariantCulture, out var value) ? value : null;

    public static void Report(IManager manager, StockResult result)
    {
        switch (result.Outcome)
        {
            case StockOutcome.Applied:
            case StockOutcome.Replayed:
                manager.Client.Notify(manager.GetTranslatedMessage("warehouse.stockApplied"), NotificationKind.Success);
                break;
            default:
                manager.Client.Notify(manager.GetTranslatedMessage($"warehouse.refusal.{result.Refusal}", result.Detail ?? ""), NotificationKind.Warning);
                break;
        }
    }
}
