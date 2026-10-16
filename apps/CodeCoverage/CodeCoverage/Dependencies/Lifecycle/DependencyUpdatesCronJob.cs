using CodeCoverage.Entities;
using CodeCoverage.LookupReferences;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark;
using MintPlayer.Spark.Cron;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Dependencies.Lifecycle;

/// <summary>
/// The scheduled half of the dependency updater (Q8, R16): every ten minutes, each repository whose settings turn the feature
/// on gets its onboarding pull request when it has none, and its scheduled run when its slot is due. Both are deduplicated per
/// repository (<c>BroadcastOnceAsync</c>: one onboarding attempt a day, one run per slot), so a tick that runs twice, or two
/// hosts, start nothing twice.
/// </summary>
/// <remarks>
/// The settings decide here; the file's opt-out and schedule parts (D-P13) are the ones the saga last read into the run
/// document, so no tick reads a file of every repository. A file that turns the feature off ends the run at its start.
/// </remarks>
public partial class DependencyUpdatesCronJob : ISparkCronJob
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IMessageBus messageBus;
    [Inject] private readonly TimeProvider time;
    [Inject] private readonly ILogger<DependencyUpdatesCronJob> logger;

    public static string CronSchedule => "*/10 * * * *";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var requestScope = session.IgnoreMaxRequests();
        var accounts = await session.Query<Account, Indexes.Accounts_Overview>()
            .Where(a => a.Connection != RepositoryConnection.Disconnected)
            .Take(1024)
            .ToListAsync(cancellationToken);
        var now = time.GetUtcNow();
        foreach (var account in accounts)
        {
            if (cancellationToken.IsCancellationRequested) break;
            try
            {
                await ScheduleAccountAsync(account, now, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Dependency updates: could not schedule the repositories of {Login}", account.Login);
            }
        }
    }

    private async Task ScheduleAccountAsync(Account account, DateTimeOffset now, CancellationToken ct)
    {
        var ownerKey = account.OwnerKey;
        var repositories = await session.Query<Repository, Indexes.Repositories_Overview>()
            .Where(r => r.OwnerKey == ownerKey)
            .Take(1024)
            .ToListAsync(ct);
        var enabled = repositories.Where(r => r.Id is not null && r.DefaultBranch is not null && r.Connection != RepositoryConnection.Disconnected && !r.Archived
            && Repository.ResolveDependencyUpdates(r, account)).ToList();
        if (enabled.Count == 0) return;
        var runs = await session.LoadAsync<DependencyUpdateRun>(enabled.Select(r => DependencyUpdateRun.ForRepository(r.Id!)), ct);
        foreach (var repository in enabled)
        {
            var run = runs.GetValueOrDefault(DependencyUpdateRun.ForRepository(repository.Id!));
            // A blocked onboarding (a permission not accepted yet) is tried again daily; one whose pull request was closed is not.
            if (run is null || run.Onboarding == EDependencyOnboarding.None
                || run.Onboarding == EDependencyOnboarding.Blocked && run.OnboardingPullRequestNumber is null)
            {
                await messageBus.BroadcastOnceAsync(new DependencyUpdateSignalMessage { RepositoryId = repository.Id!, Kind = EDependencySignal.Onboard },
                    $"dependency-onboard:{repository.Id}:{now.UtcDateTime:yyyy-MM-dd}", ct);
            }
            if (run?.FileDisabled == true) continue;

            var (settings, _) = Repository.ResolveDependencySchedule(repository, account);
            var schedule = settings with
            {
                Frequency = run?.FileFrequency ?? settings.Frequency,
                Weekday = run?.FileWeekday ?? settings.Weekday,
                Hour = run?.FileHour ?? settings.Hour,
            };
            var (due, key) = DependencySlots.Due(schedule, now, repository.Id!);
            if (due)
                await messageBus.BroadcastOnceAsync(new DependencyUpdateSignalMessage
                {
                    RepositoryId = repository.Id!, Kind = EDependencySignal.Trigger, Trigger = EDependencyRunTrigger.Scheduled,
                }, key, ct);
        }
    }
}
