using Microsoft.Extensions.Options;
using MintPlayer.Spark.Commerce.Ticketing.Events.Models;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Contracts;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Models;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Secrets;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Services;
using MintPlayer.Spark.Organizations;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Ticketing.Tickets.Scanner;

/// <summary>
/// Door devices (§4.11): a member holding the <c>scanner</c> custom role (X8), or an Admin, registers a device for one
/// event and list; the device gets a token once and sends it as <c>X-Spark-Scanner: {key}.{token}</c>. The token is
/// stored as a SHA-256 hash (256 random bits need no stretching), works until the event's last date plus the grace, and
/// stops at once when revoked: the device then wipes its offline list.
/// </summary>
internal sealed class ScannerDevices(
    IDocumentStore store,
    TicketAccess access,
    ISparkOrganizations organizations,
    IOptionsMonitor<SparkTicketingTicketsOptions> options,
    TimeProvider clock)
{
    public const string HeaderName = "X-Spark-Scanner";

    private const string Prefix = "TicketScannerDevices/";

    /// <summary>Whether the caller may run the door of <paramref name="organizationId"/>: the scanner role, or Admin and up.</summary>
    public async Task<bool> MayScanAsync(string? organizationId, CancellationToken cancellationToken)
    {
        if (access.UserId is not { } me || organizationId is null)
            return false;
        var role = await organizations.GetRoleAsync(organizationId, me, cancellationToken);
        if (role is null)
            return false;
        return role >= OrganizationRole.Admin
            || (await organizations.GetCustomRolesAsync(organizationId, me, cancellationToken)).Contains(options.CurrentValue.Scanner.Role, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<SparkScannerDeviceRegistered?> RegisterAsync(SparkScannerDeviceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var session = store.OpenAsyncSession();
        var ev = await session.LoadAsync<TicketEvent>(request.EventId, cancellationToken);
        if (ev is null || !await MayScanAsync(ev.OrganizationId, cancellationToken))
            return null;
        var lists = await TicketCatalog.ListsAsync(session, ev, cancellationToken);
        var list = request.ListId is { Length: > 0 } listId ? lists.FirstOrDefault(l => l.Id == listId) : lists.FirstOrDefault();
        if (list is null)
            throw TicketException.Of(TicketRefusals.ListNotFound, request.ListId);
        var eventId = ev.Id!;
        var dates = await session.Query<TicketEventDate>().Where(d => d.EventId == eventId).ToListAsync(cancellationToken);
        if (request.DateId is { Length: > 0 } dateId && !dates.Any(d => d.Id == dateId))
            throw TicketException.Of(TicketRefusals.ListNotFound, dateId);

        var now = clock.GetUtcNow();
        var key = Guid.NewGuid().ToString("N");
        var token = TicketSecrets.NewToken();
        var device = new TicketScannerDevice
        {
            Id = Prefix + key,
            OrganizationId = ev.OrganizationId,
            EventId = eventId,
            ListId = list.Id!,
            DateId = string.IsNullOrEmpty(request.DateId) ? null : request.DateId,
            Name = string.IsNullOrWhiteSpace(request.Name) ? "Scanner" : request.Name.Trim()[..Math.Min(80, request.Name.Trim().Length)],
            TokenHash = TicketSecrets.TokenHash(token),
            CreatedBy = access.UserId!,
            CreatedAt = now,
            ExpiresAt = TicketTimes.EventEnd(dates, now) + options.CurrentValue.Scanner.TokenGraceAfterEvent,
        };
        await session.StoreAsync(device, device.Id, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        return new SparkScannerDeviceRegistered(device.Id, key, token, device.ExpiresAt);
    }

    public async Task<bool> RevokeAsync(string deviceId, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var device = await session.LoadAsync<TicketScannerDevice>(deviceId, cancellationToken);
        if (device is null || !await MayScanAsync(device.OrganizationId, cancellationToken))
            return false;
        if (!device.Revoked)
        {
            device.Revoked = true;
            device.RevokedAt = clock.GetUtcNow();
            await session.SaveChangesAsync(cancellationToken);
        }
        return true;
    }

    /// <summary>
    /// The device behind <paramref name="header"/>; null for an unknown key or a wrong token. A revoked or expired device
    /// throws <see cref="TicketRefusals.DeviceRevoked"/>: the client wipes its list.
    /// </summary>
    public async Task<TicketScannerDevice?> AuthenticateAsync(string? header, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(header))
            return null;
        var dot = header.IndexOf('.');
        if (dot is < 1 or > 64)
            return null;
        var key = header[..dot];
        var token = header[(dot + 1)..];
        if (!key.All(char.IsAsciiHexDigitLower))
            return null;
        using var session = store.OpenAsyncSession();
        var device = await session.LoadAsync<TicketScannerDevice>(Prefix + key, cancellationToken);
        if (device is null || !TicketSecrets.TokenMatches(token, device.TokenHash))
            return null;
        var now = clock.GetUtcNow();
        if (device.Revoked || device.ExpiresAt <= now)
            throw TicketException.Of(TicketRefusals.DeviceRevoked);
        if (device.LastSeenAt is not { } seen || now - seen > TimeSpan.FromMinutes(1))
        {
            device.LastSeenAt = now;
            await session.SaveChangesAsync(cancellationToken);
        }
        return device;
    }
}
