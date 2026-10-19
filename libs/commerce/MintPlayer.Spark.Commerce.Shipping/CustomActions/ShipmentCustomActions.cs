using System.Globalization;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Abstractions.Retry;

namespace MintPlayer.Spark.Commerce.Shipping.CustomActions;

/// <summary>The custom actions of <c>Shipment</c>, prefixed with the type's name (custom-action names are first-wins across assemblies).</summary>
public static class ShipmentActionNames
{
    public const string VoidLabel = "ShipmentVoidLabel";
    public const string CreateReturnLabel = "ShipmentCreateReturnLabel";
    public const string AddParcel = "ShipmentAddParcel";
    public const string ReleaseBooking = "ShipmentReleaseBooking";
}

/// <summary>
/// Staff confirm, after checking the carrier's own panel, that an in-doubt booking does not exist there (carriers
/// that cannot look a booking up): the next attempt books again. Confirmation in actions.json.
/// </summary>
internal sealed partial class ShipmentReleaseBookingAction : ICustomAction
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly ISparkShipping shipping;
    [Inject] private readonly ISparkCurrentUser currentUser;

    public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        if (args.Parent?.Id is not { Length: > 0 } id)
            return;
        var released = await shipping.ReleasePendingBookingAsync(id, currentUser.Id, cancellationToken);
        manager.Client.Notify(manager.GetTranslatedMessage(released ? "commerceShipping.bookingReleased" : "commerceShipping.bookingNotInDoubt"),
            released ? NotificationKind.Success : NotificationKind.Warning);
        manager.Client.RefreshQuery("shipments");
    }
}

/// <summary>Voids every live parcel's label (before handover). Confirmation in actions.json.</summary>
internal sealed partial class ShipmentVoidLabelAction : ICustomAction
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly ISparkShipping shipping;
    [Inject] private readonly ISparkCurrentUser currentUser;

    public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        // The framework loaded and row-checked the parent before this runs.
        if (args.Parent?.Id is not { Length: > 0 } id)
            return;
        var voided = await shipping.VoidAsync(id, null, currentUser.Id, cancellationToken);
        manager.Client.Notify(manager.GetTranslatedMessage(voided ? "commerceShipping.labelVoided" : "commerceShipping.labelNotVoided"),
            voided ? NotificationKind.Success : NotificationKind.Warning);
        manager.Client.RefreshQuery("shipments");
    }
}

/// <summary>Creates a return label for the shipment (GB5), asking the reason and weight in <c>ShipmentReturnInput</c>.</summary>
internal sealed partial class ShipmentCreateReturnLabelAction : ICustomAction
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly ISparkShipping shipping;
    [Inject] private readonly ISparkCurrentUser currentUser;

    public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        if (args.Parent?.Id is not { Length: > 0 } id)
            return;
        var form = await manager.GetPersistentObjectAsync("ShipmentReturnInput", cancellationToken: cancellationToken);
        var confirm = manager.GetTranslatedMessage("commerceShipping.confirm");
        manager.Retry.Action(
            title: manager.GetTranslatedMessage("commerceShipping.returnTitle"),
            options: [confirm],
            defaultOption: confirm,
            persistentObject: form,
            cancellable: true);
        var answer = manager.Retry.Result!;
        if (answer.Option == RetryResult.CancelOption || answer.PersistentObject is not { } filled)
            return;

        var reason = ShipmentInputs.Text(filled, "Reason");
        var weight = ShipmentInputs.Grams(filled, "WeightGrams");
        try
        {
            await shipping.CreateReturnLabelAsync(new ReturnLabelRequest
            {
                ShipmentId = id,
                Reason = reason,
                Parcels = weight is > 0 ? [new ParcelRequest(weight.Value)] : null,
                CreatedBy = currentUser.Id,
            }, cancellationToken);
            manager.Client.Notify(manager.GetTranslatedMessage("commerceShipping.returnCreated"), NotificationKind.Success);
        }
        catch (ShippingRequestException ex)
        {
            manager.Client.Notify(string.Join(" ", ex.Problems), NotificationKind.Warning);
        }
        catch (ShipmentBookingInDoubtException ex)
        {
            manager.Client.Notify(ex.Message, NotificationKind.Warning);
        }
        catch (CarrierException ex)
        {
            manager.Client.Notify(ex.Message, NotificationKind.Error);
        }
        manager.Client.RefreshQuery("shipments");
    }
}

/// <summary>Adds a parcel with its own label to the shipment (GB5), asking its weight and size in <c>ShipmentParcelInput</c>.</summary>
internal sealed partial class ShipmentAddParcelAction : ICustomAction
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly ISparkShipping shipping;
    [Inject] private readonly ISparkCurrentUser currentUser;

    public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        if (args.Parent?.Id is not { Length: > 0 } id)
            return;
        var form = await manager.GetPersistentObjectAsync("ShipmentParcelInput", cancellationToken: cancellationToken);
        var confirm = manager.GetTranslatedMessage("commerceShipping.confirm");
        manager.Retry.Action(
            title: manager.GetTranslatedMessage("commerceShipping.addParcelTitle"),
            options: [confirm],
            defaultOption: confirm,
            persistentObject: form,
            cancellable: true);
        var answer = manager.Retry.Result!;
        if (answer.Option == RetryResult.CancelOption || answer.PersistentObject is not { } filled)
            return;
        var weight = ShipmentInputs.Grams(filled, "WeightGrams");
        if (weight is not > 0)
        {
            manager.Client.Notify(manager.GetTranslatedMessage("commerceShipping.weightRequired"), NotificationKind.Warning);
            return;
        }
        try
        {
            await shipping.AddParcelAsync(id, new ParcelRequest(weight.Value,
                ShipmentInputs.Grams(filled, "LengthMm") ?? 0,
                ShipmentInputs.Grams(filled, "WidthMm") ?? 0,
                ShipmentInputs.Grams(filled, "HeightMm") ?? 0), currentUser.Id, cancellationToken);
            manager.Client.Notify(manager.GetTranslatedMessage("commerceShipping.parcelAdded"), NotificationKind.Success);
        }
        catch (ShippingRequestException ex)
        {
            manager.Client.Notify(string.Join(" ", ex.Problems), NotificationKind.Warning);
        }
        catch (ShipmentBookingInDoubtException ex)
        {
            manager.Client.Notify(ex.Message, NotificationKind.Warning);
        }
        catch (CarrierException ex)
        {
            manager.Client.Notify(ex.Message, NotificationKind.Error);
        }
        manager.Client.RefreshQuery("shipments");
    }
}

/// <summary>Reads the prompt forms' values.</summary>
internal static class ShipmentInputs
{
    public static string? Text(PersistentObject form, string name)
        => form.TryGetAttribute(name, out var attribute) && attribute.Value?.ToString() is { } text && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    public static int? Grams(PersistentObject form, string name)
        => Text(form, name) is { } text && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0 ? value : null;
}
