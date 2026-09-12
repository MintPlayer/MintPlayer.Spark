using Fleet.Entities;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Actions;
using Raven.Client.Documents.Session;

namespace Fleet.CustomActions;

/// <summary>
/// Demo seeder: stamps every car with a random registration timestamp carrying a deliberately varied
/// timezone offset, so the <c>Registrations</c> grid shows offsets that could not have survived by
/// accident.
/// </summary>
/// <remarks>
/// <para>
/// This exists to make a data-fidelity fix <em>visible</em>. RavenDB converts a
/// <see cref="DateTimeOffset"/> to its UTC-equivalent <c>DateTime</c> whenever the value becomes a
/// scalar index field, so before the fix every row in that grid came back <c>+00:00</c> — the right
/// instant, the wrong wall clock, and no way to tell which country's morning it was.
/// </para>
/// <para>
/// <strong>The offsets below are the whole point.</strong> A demo seeded with UTC would prove nothing:
/// <see cref="TimeSpan.Zero"/> round-trips correctly even when the fix is absent, which is precisely
/// why the defect survived a 2000-test suite for years. So the set is mixed-sign, includes a
/// fractional offset, and includes exactly one <c>+00:00</c> — the control.
/// </para>
/// <para>
/// Operates on the caller's whole collection, not on a selection: <c>selectionRule</c> is <c>=0</c> and
/// <see cref="CustomActionArgs.SelectedItems"/> is never read.
/// </para>
/// </remarks>
public partial class ScatterRegistrationOffsetsAction : SparkCustomAction
{
    [Inject] private readonly IDatabaseAccess dbAccess;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IManager manager;

    /// <summary>
    /// Mixed sign, one fractional, one zero. Nepal (+05:45) and Newfoundland (-03:30) are real offsets,
    /// not contrivances — they are what makes "just store minutes" less obviously sufficient than it
    /// looks, and they render unmistakably in a grid.
    /// </summary>
    private static readonly TimeSpan[] Offsets =
    [
        TimeSpan.FromMinutes(120),   // +02:00  Brussels, summer
        TimeSpan.FromMinutes(-480),  // -08:00  Seattle
        TimeSpan.FromMinutes(345),   // +05:45  Kathmandu
        TimeSpan.Zero,               // +00:00  the control: correct even before the fix
        TimeSpan.FromMinutes(-210),  // -03:30  St. John's
        TimeSpan.FromMinutes(570),   // +09:30  Adelaide
    ];

    public override async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        // Unchecked on purpose: this is a demo seeder over the whole collection, and CarActions'
        // row filter would otherwise scope it to the caller's own cars -- leaving most of the grid
        // unstamped and the demo looking broken.
        var cars = (await dbAccess.GetDocumentsUncheckedAsync<Car>()).ToList();
        if (cars.Count == 0)
        {
            manager.Client.Notify("No cars to stamp — create a few first.", NotificationKind.Warning);
            return;
        }

        var random = new Random();
        foreach (var car in cars)
        {
            var offset = Offsets[random.Next(Offsets.Length)];
            var wallClock = new DateTime(2026, 1, 1).AddMinutes(random.Next(0, 365 * 24 * 60));

            // new DateTimeOffset(wallClock, offset) means "this wall clock, in that zone" -- the
            // instant differs per offset, which is what makes the sort worth looking at.
            car.RegisteredAt = new DateTimeOffset(wallClock, offset);
        }

        // Mutate the tracked entities and save once. dbAccess.SaveDocumentUncheckedAsync calls
        // SaveChangesAsync per document, which is a round trip per car.
        //
        // Waiting for indexes matters here: refreshOnCompleted re-runs the query the moment this
        // returns, and Cars_Overview is eventually consistent -- without the wait the grid can
        // redraw the pre-click values and the button looks like it did nothing.
        session.Advanced.WaitForIndexesAfterSaveChanges(TimeSpan.FromSeconds(15));
        await session.SaveChangesAsync(cancellationToken);

        var distinct = cars.Select(c => c.RegisteredAt.Offset).Distinct().Count();
        manager.Client.Notify(
            $"Stamped {cars.Count} cars with {distinct} different UTC offsets. Sort the Registered column — " +
            $"the order is by instant, while each row keeps its own local wall clock.",
            NotificationKind.Success);
    }
}
