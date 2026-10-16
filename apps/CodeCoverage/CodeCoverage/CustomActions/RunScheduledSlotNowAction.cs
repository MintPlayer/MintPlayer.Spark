using CodeCoverage.Dependencies.Lifecycle;
using CodeCoverage.Dependencies.Onboarding;
using CodeCoverage.Entities;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents.Session;

namespace CodeCoverage.CustomActions;

/// <summary>
/// "Run the scheduled slot now" (dependency-updates PRD R28, S18-D4), on a Repository's page: a run exactly like the
/// scheduled one, which is the only kind that may auto-merge (D-L), started now. For R21's people (a manager of the account,
/// or an administrator of the repository on the forge, checked now). Refused while a run is in progress for the repository.
/// </summary>
/// <remarks>
/// It broadcasts <c>Trigger(Scheduled)</c> with a fresh deduplication key, expedited (a person is waiting). It does not
/// touch the messaging state, unlike editing <c>SparkMessages</c> by hand, which S18 rejected as unsafe.
/// </remarks>
public partial class RunScheduledSlotNowAction : SparkCustomAction
{
    [Inject] private readonly IDependencyOnboardingService onboarding;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IMessageBus messageBus;
    [Inject] private readonly IManager manager;
    [Inject] private readonly TimeProvider time;

    public override async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        if (args.Parent?.Id is not { Length: > 0 } id || await session.LoadAsync<Repository>(id, cancellationToken) is not { } repository)
            return;
        var outcome = await RunAsync(repository, cancellationToken);
        args.SetResult(new RunScheduledSlotNowResult(outcome));
        manager.Client.Notify(manager.GetTranslatedMessage($"dependencyOnboarding.runNow.{outcome}"), outcome == Started ? NotificationKind.Success : NotificationKind.Warning);
    }

    public const string Started = "started";
    public const string Refused = "refused";
    public const string Busy = "busy";
    public const string Off = "off";

    /// <summary>The run, or why not: <see cref="Started"/>, <see cref="Refused"/> (not R21's), <see cref="Off"/> (the feature
    /// is off here) or <see cref="Busy"/> (a run is in progress).</summary>
    internal async Task<string> RunAsync(Repository repository, CancellationToken ct)
    {
        if ((await onboarding.CheckAsync([repository], ct)).GetValueOrDefault(repository.Id!) is null)
            return Refused;
        var account = repository.Account is { } accountId ? await session.LoadAsync<Account>(accountId, ct) : null;
        if (!Repository.ResolveDependencyUpdates(repository, account))
            return Off;
        if (await session.LoadAsync<DependencyUpdateRun>(DependencyUpdateRun.ForRepository(repository.Id!), ct) is { } run && run.State.IsBusy())
            return Busy;
        await messageBus.BroadcastOnceAsync(new DependencyUpdateSignalMessage
        {
            RepositoryId = repository.Id!, Kind = EDependencySignal.Trigger, Trigger = EDependencyRunTrigger.Scheduled,
        }, $"dependency-run-now:{repository.Id}:{time.GetUtcNow().UtcTicks}:{Guid.NewGuid():N}", new BroadcastOptions { Expedite = true }, ct);
        return Started;
    }
}

public sealed record RunScheduledSlotNowResult(string Outcome);
