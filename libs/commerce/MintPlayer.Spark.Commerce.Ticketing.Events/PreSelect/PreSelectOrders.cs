using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Visitor;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Checkout.Contracts;
using MintPlayer.Spark.Commerce.Ticketing.Events.Contracts;
using MintPlayer.Spark.Commerce.Ticketing.Events.Models;
using MintPlayer.Spark.Commerce.Ticketing.Events.Sales;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Ticketing.Events.PreSelect;

/// <summary>What one attempt to put an alternative in the basket gave.</summary>
/// <param name="BasketLineId">The new basket line, when it was added.</param>
/// <param name="Refusal">The checkout refusal code otherwise.</param>
public sealed record PreSelectAttempt(string? BasketLineId, string? Refusal)
{
    public bool Added => BasketLineId is not null;
}

/// <summary>
/// D25: holds a pre-selection at admission. For each line, the alternatives are tried in order; an alternative that is
/// short of places or over the buyer's limit is retried with one ticket less ("clamped per line to availability and
/// limits") before the next alternative is tried. Pure: <c>tryAdd</c> does the claiming.
/// </summary>
public static class PreSelectPlanner
{
    /// <summary>Refusals a smaller quantity may cure.</summary>
    private static readonly HashSet<string> Shortages = new(StringComparer.Ordinal)
    {
        CheckoutRefusals.SoldOut, CheckoutRefusals.LimitExceeded, CheckoutRefusals.NotTogether, CheckoutRefusals.UnitTaken, CheckoutRefusals.LeavesSingleSeat,
    };

    public static async Task<List<PreSelectApplied>> ApplyAsync(IReadOnlyList<PreSelectLine> lines, Func<PreSelectChoice, int, Task<PreSelectAttempt>> tryAdd)
    {
        var applied = new List<PreSelectApplied>();
        for (var l = 0; l < lines.Count; l++)
        {
            var done = false;
            for (var a = 0; a < lines[l].Alternatives.Count && !done; a++)
            {
                var choice = lines[l].Alternatives[a];
                for (var quantity = choice.Quantity; quantity >= 1; quantity--)
                {
                    var attempt = await tryAdd(choice, quantity);
                    if (attempt.Added)
                    {
                        applied.Add(new PreSelectApplied { Line = l, Alternative = a, BasketLineId = attempt.BasketLineId!, Quantity = quantity });
                        done = true;
                        break;
                    }
                    if (attempt.Refusal is null || !Shortages.Contains(attempt.Refusal))
                        break;
                }
            }
        }
        return applied;
    }
}

/// <summary>
/// PreSelectOrder (D25, PRD §4.11): a buyer pre-selects before admission (ticket type × price category or block ×
/// quantity, with ranked alternatives). When the waiting room admits them, the client calls <see cref="ApplyAsync"/>
/// with the admission token: the pre-selection is put in the basket, so it is held at once in a HoldFromSelection
/// channel and only pre-filled in an OnPayment one. The presence check is the waiting room's: it issues the token only
/// to a present tab (§4.12). <see cref="ReleaseAsync"/> gives everything back in one click.
/// </summary>
public interface IPreSelectOrders
{
    Task<SparkPreSelection?> GetAsync(string phaseId, CancellationToken cancellationToken = default);

    Task<SparkPreSelection> SaveAsync(SparkPreSelectionRequest request, CancellationToken cancellationToken = default);

    Task<SparkPreSelection> ApplyAsync(SparkPreSelectApplyRequest request, CancellationToken cancellationToken = default);

    Task<SparkPreSelection?> ReleaseAsync(string phaseId, CancellationToken cancellationToken = default);
}

internal sealed class PreSelectOrders(
    IDocumentStore store,
    ISparkCheckout checkout,
    IServiceProvider services,
    IOptionsMonitor<SparkTicketingEventsOptions> options,
    TimeProvider clock) : IPreSelectOrders
{
    private ISparkVisitor? Visitor => services.GetService<ISparkVisitor>();

    private string? IdentityKey => Visitor switch
    {
        { UserId: { Length: > 0 } user } => "user:" + user,
        { VisitorId: { Length: > 0 } visitor } => "visitor:" + visitor,
        _ => null,
    };

    internal static string IdFor(string phaseId, string identityKey)
        => $"ticketing-preselections/{phaseId}/{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identityKey)))[..32]}";

    public async Task<SparkPreSelection?> GetAsync(string phaseId, CancellationToken cancellationToken = default)
    {
        if (IdentityKey is not { } identity)
            return null;
        using var session = store.OpenAsyncSession();
        var selection = await session.LoadAsync<TicketPreSelection>(IdFor(phaseId, identity), cancellationToken);
        return selection is null ? null : View(selection);
    }

    public async Task<SparkPreSelection> SaveAsync(SparkPreSelectionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var identity = await EnsureIdentityAsync();
        var settings = options.CurrentValue.PreSelect;
        using var session = store.OpenAsyncSession();
        var phase = await RequirePhaseAsync(session, request.PhaseId, cancellationToken);
        if (request.Lines.Count > settings.MaxLines)
            throw CheckoutException.Of(CheckoutRefusals.InvalidQuantity, subject: "lines");
        var lines = new List<PreSelectLine>();
        foreach (var line in request.Lines)
        {
            if (line.Alternatives.Count == 0 || line.Alternatives.Count > settings.MaxAlternatives)
                throw CheckoutException.Of(CheckoutRefusals.InvalidQuantity, subject: "alternatives");
            var alternatives = new List<PreSelectChoice>();
            foreach (var choice in line.Alternatives)
            {
                if (TicketItemIds.Parse(choice.ItemId) is not { } item || item.PhaseId != phase.Id || choice.Quantity < 1)
                    throw CheckoutException.Of(CheckoutRefusals.ItemNotFound, subject: choice.ItemId);
                alternatives.Add(new PreSelectChoice
                {
                    ItemId = choice.ItemId,
                    Quantity = choice.Quantity,
                    Options = choice.Options is null ? new(StringComparer.Ordinal) : new(choice.Options.Where(o => o.Key is TicketOptions.Chain or TicketOptions.Block or TicketOptions.Presale), StringComparer.Ordinal),
                });
            }
            lines.Add(new PreSelectLine { Alternatives = alternatives });
        }

        var id = IdFor(phase.Id!, identity);
        var selection = await session.LoadAsync<TicketPreSelection>(id, cancellationToken)
            ?? new TicketPreSelection { Id = id, PhaseId = phase.Id!, EventId = phase.EventId, IdentityKey = identity };
        selection.Lines = lines;
        selection.UpdatedAt = clock.GetUtcNow();
        await session.StoreAsync(selection, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        return View(selection);
    }

    public async Task<SparkPreSelection> ApplyAsync(SparkPreSelectApplyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var identity = IdentityKey ?? throw CheckoutException.Of(CheckoutRefusals.ItemNotFound, subject: "preselection");
        using var session = store.OpenAsyncSession();
        var selection = await session.LoadAsync<TicketPreSelection>(IdFor(request.PhaseId, identity), cancellationToken)
            ?? throw CheckoutException.Of(CheckoutRefusals.ItemNotFound, subject: "preselection");

        // Idempotent: a second call while the held lines are still in the basket answers the same.
        var basket = await checkout.GetBasketAsync(cancellationToken);
        if (selection.Applied.Count > 0 && selection.Applied.All(a => basket.Lines.Any(l => l.LineId == a.BasketLineId)))
            return View(selection);

        selection.Applied = await PreSelectPlanner.ApplyAsync(selection.Lines, async (choice, quantity) =>
        {
            var before = (await checkout.GetBasketAsync(cancellationToken)).Lines.Select(l => (l.LineId, l.Quantity)).ToList();
            try
            {
                var after = await checkout.AddLineAsync(new SparkBasketAddRequest(choice.ItemId, quantity,
                    Options: choice.Options.Count == 0 ? null : new Dictionary<string, string>(choice.Options), AdmissionTokens: request.AdmissionToken is { Length: > 0 } token
                        ? new Dictionary<string, string> { [TicketScopes.Admission(request.PhaseId)] = token }
                        : null), cancellationToken);
                // The new line, or (when checkout merged it into an equal line) the line whose quantity grew.
                var line = after.Lines.FirstOrDefault(l => !before.Any(b => b.LineId == l.LineId))
                    ?? after.Lines.FirstOrDefault(l => before.Any(b => b.LineId == l.LineId && b.Quantity < l.Quantity));
                return new PreSelectAttempt(line?.LineId, line is null ? CheckoutRefusals.Conflict : null);
            }
            catch (CheckoutException ex)
            {
                return new PreSelectAttempt(null, ex.Code);
            }
        });
        selection.AppliedAt = clock.GetUtcNow();
        await session.SaveChangesAsync(cancellationToken);
        return View(selection);
    }

    public async Task<SparkPreSelection?> ReleaseAsync(string phaseId, CancellationToken cancellationToken = default)
    {
        if (IdentityKey is not { } identity)
            return null;
        using var session = store.OpenAsyncSession();
        var selection = await session.LoadAsync<TicketPreSelection>(IdFor(phaseId, identity), cancellationToken);
        if (selection is null)
            return null;
        var basket = await checkout.GetBasketAsync(cancellationToken);
        foreach (var applied in selection.Applied)
        {
            // A line checkout merged into one the buyer already had keeps the buyer's own tickets.
            if (basket.Lines.FirstOrDefault(l => l.LineId == applied.BasketLineId) is not { } line)
                continue;
            try
            {
                await checkout.SetQuantityAsync(applied.BasketLineId, Math.Max(0, line.Quantity - applied.Quantity), cancellationToken);
            }
            catch (CheckoutException ex) when (ex.Code == CheckoutRefusals.LineNotFound)
            {
                // Already gone (removed by the buyer, or its hold lapsed and the line was dropped).
            }
        }
        selection.Applied = [];
        selection.AppliedAt = null;
        await session.SaveChangesAsync(cancellationToken);
        return View(selection);
    }

    private async Task<string> EnsureIdentityAsync()
    {
        if (IdentityKey is { } identity)
            return identity;
        var visitor = Visitor ?? throw new InvalidOperationException("No visitor identity: register Spark's visitor feature (X1).");
        return "visitor:" + await visitor.EnsureAsync();
    }

    private static async Task<TicketSalePhase> RequirePhaseAsync(Raven.Client.Documents.Session.IAsyncDocumentSession session, string phaseId, CancellationToken cancellationToken)
    {
        var phase = await session.LoadAsync<TicketSalePhase>(phaseId, cancellationToken);
        var ev = phase is null ? null : await session.LoadAsync<TicketEvent>(phase.EventId, cancellationToken);
        if (phase is null || ev is null || !ev.IsPublished || !phase.PreSelect)
            throw CheckoutException.Of(CheckoutRefusals.ItemNotFound, subject: phaseId);
        return phase;
    }

    private static SparkPreSelection View(TicketPreSelection selection) => new(
        selection.PhaseId,
        selection.Lines.Select(l => new SparkPreSelectLine(l.Alternatives.Select(a => new SparkPreSelectChoice(a.ItemId, a.Quantity, a.Options.Count == 0 ? null : new Dictionary<string, string>(a.Options))).ToList())).ToList(),
        selection.Applied.Select(a => new SparkPreSelectApplied(a.Line, a.Alternative, a.BasketLineId, a.Quantity)).ToList(),
        selection.AppliedAt);
}
