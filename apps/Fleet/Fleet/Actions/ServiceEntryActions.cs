using Fleet.Entities;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;

namespace Fleet.Actions;

/// <summary>
/// The server-side row lifecycle (#386), demonstrated on a real embedded collection: what a new
/// service entry starts out looking like, and when one may not be removed.
/// </summary>
/// <remarks>
/// Both hooks only run because <c>App_Data/Model/ServiceEntry.json</c> sets
/// <c>serverSideRowLifecycle</c>. Without the flag the grid never asks and these methods are dead
/// code — which is the design: opting in is per row type and costs a request per click, so it is not
/// something to switch on for a type with nothing to say.
/// </remarks>
public partial class ServiceEntryActions : DefaultPersistentObjectActions<ServiceEntry>
{
    /// <summary>
    /// Gives a new entry today's date and a description that names the vehicle, so the common case
    /// is filling in the cost and nothing else.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b><see cref="PersistentObjectAttribute.SetOriginalValue{TValue}"/>, not
    /// <see cref="PersistentObjectAttribute.SetValue{TValue}"/>.</b> The latter marks the attribute
    /// changed, which would make the form dirty before the user has typed anything — so opening a
    /// car, clicking Add and clicking away would raise an unsaved-changes prompt over a car nobody
    /// edited.
    /// <para>
    /// The plate is read from the parent, which is what <see cref="SparkNewArgs{T}.AsDetailParent"/>
    /// is for, and read defensively: it is null when the vehicle has never been saved, and the
    /// indexer on <see cref="PersistentObject"/> throws rather than returning null for an attribute
    /// that is not there. A construction hook that throws turns an Add button into a 500.
    /// </para>
    /// </remarks>
    public override Task OnNewAsync(SparkNewArgs<ServiceEntry> args)
    {
        var entry = args.PersistentObject;
        entry[nameof(ServiceEntry.PerformedOn)].SetOriginalValue(DateOnly.FromDateTime(DateTime.Today));

        var plate = args.AsDetailParent?.Attributes
            .FirstOrDefault(a => a.Name == nameof(Car.LicensePlate))?.Value?.ToString();

        if (!string.IsNullOrWhiteSpace(plate))
            entry[nameof(ServiceEntry.Description)].SetOriginalValue($"Service — {plate}");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Refuses to release an entry that has already been invoiced.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Reads <see cref="SparkDeleteRowArgs{T}.Row"/>, which is the STORED row</b> — never the
    /// client's copy. That is the difference between a rule and a suggestion: had this read the
    /// submitted row, a caller would only have to send <c>IsInvoiced = false</c> to be allowed
    /// through, and the check would be consulting the very claim it exists to doubt.
    /// <para>
    /// ⚠️ And it is still an affordance rather than enforcement. A caller who never calls the
    /// endpoint and saves the car with the entry already gone does not pass through here at all.
    /// What covers that caller is <c>Delete/ServiceEntry</c>, which the save path applies to every
    /// embedded collection unconditionally. A refusal here is how a user finds out <em>why</em>; the
    /// right is what makes it true.
    /// </para>
    /// </remarks>
    public override Task OnDeleteRowAsync(SparkDeleteRowArgs<ServiceEntry> args)
    {
        // The mapper emits a bool as a bool, but a value that has been through JSON can arrive as a
        // string — so compare on both rather than casting and hoping.
        var invoiced = args.Row.Attributes
            .FirstOrDefault(a => a.Name == nameof(ServiceEntry.IsInvoiced))?.Value;

        if (invoiced is true || string.Equals(invoiced?.ToString(), "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new SparkValidationException(
                "This service entry has already been invoiced and cannot be removed. "
                + "Add a correcting entry instead.",
                nameof(Car.ServiceEntries));
        }

        return Task.CompletedTask;
    }
}
