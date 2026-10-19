using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Models;

namespace MintPlayer.Spark.Commerce.Ticketing.Venues.Services;

/// <summary>
/// A venue saved through Spark (a form, or an approved proposal, X7): the proposer is recorded on creation and kept
/// afterwards, and the venue's zone follows its address (D33: a venue's zone comes from its address; K10: nothing is
/// converted here).
/// </summary>
internal sealed class VenueSaveInterceptor(ISparkCurrentUser currentUser, TimeProvider clock) : IBeforeSave<Venue>
{
    public ValueTask OnBeforeSaveAsync(Venue entity, SaveContext context)
    {
        if (context.Before is Venue before)
        {
            entity.CreatedBy = before.CreatedBy;
            entity.CreatedAtUtc = before.CreatedAtUtc;
        }
        else
        {
            entity.CreatedBy = context.IsReplacedSaveApply ? context.OnBehalfOf : currentUser.Id;
            entity.CreatedAtUtc = clock.GetUtcNow().UtcDateTime;
        }
        entity.Name = entity.Name?.Trim() ?? "";
        if (entity.Name.Length == 0)
            throw new SparkValidationException("ticketingVenues.problem.name", nameof(Venue.Name));
        entity.TimeZone = entity.Address?.TimeZone is { Length: > 0 } zone ? zone : null;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A layout saved through Spark: only its name is a form field. Layouts are created by <c>POST /layouts/create</c>
/// (never by the form), and only the owner renames one (an approved proposal applies on the owner's behalf, D8).
/// Everything else is restored from the stored layout, so a form cannot move ownership, state or versions.
/// </summary>
internal sealed class VenueLayoutSaveInterceptor(ISparkCurrentUser currentUser) : IBeforeSave<VenueLayout>
{
    public ValueTask OnBeforeSaveAsync(VenueLayout entity, SaveContext context)
    {
        if (context.IsSystemContext)
            return ValueTask.CompletedTask;
        if (context.Before is not VenueLayout before)
            throw new SparkAccessDeniedException(VenueAccess.LayoutType);
        var actor = context.IsReplacedSaveApply ? context.OnBehalfOf : currentUser.Id;
        if (!string.Equals(actor, before.OwnerId, StringComparison.Ordinal))
            throw new SparkAccessDeniedException(VenueAccess.LayoutType);

        var name = entity.Name?.Trim() ?? "";
        if (name.Length == 0 || name.Length > 120)
            throw new SparkValidationException("ticketingVenues.problem.name", nameof(VenueLayout.Name));

        entity.VenueId = before.VenueId;
        entity.OwnerId = before.OwnerId;
        entity.CopiedFromId = before.CopiedFromId;
        entity.State = before.State;
        entity.PublishedVersion = before.PublishedVersion;
        entity.DraftRevision = before.DraftRevision;
        entity.PendingRequestId = before.PendingRequestId;
        entity.LastRejection = before.LastRejection;
        entity.SeatCount = before.SeatCount;
        entity.UnnumberedCapacity = before.UnnumberedCapacity;
        entity.BlockCount = before.BlockCount;
        entity.UpdatedAtUtc = before.UpdatedAtUtc;
        entity.Name = name;
        return ValueTask.CompletedTask;
    }
}
