using System.Reflection;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Approvals.Proposals;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Approvals.Services;

/// <summary>
/// The kinds this application knows, found from the registrations themselves: every closed
/// <see cref="IApprovalDeciders{TKind}"/> in the container is a kind. So a kind needs no registration of its own,
/// whether its deciders were discovered (<c>[SparkDiscoverable]</c>) or registered by a library.
/// </summary>
/// <remarks>
/// It reads the <see cref="IServiceCollection"/> it was created over, lazily: the container is complete by the time
/// anything asks.
/// </remarks>
internal sealed class ApprovalKindCatalog(IServiceCollection services)
{
    private readonly Lazy<IReadOnlyDictionary<string, IApprovalKindRuntime>> kinds = new(() => Scan(services));

    /// <summary>The kind stored as <paramref name="name"/>; null for an unknown (removed) kind.</summary>
    public IApprovalKindRuntime? Find(string name) => kinds.Value.TryGetValue(name, out var kind) ? kind : null;

    public IEnumerable<IApprovalKindRuntime> All => kinds.Value.Values;

    /// <summary>Every problem with the registrations; startup is refused on any.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        var counts = services
            .Where(d => d.ServiceType.IsGenericType && !d.ServiceType.IsGenericTypeDefinition)
            .GroupBy(d => d.ServiceType)
            .Where(g => g.Key.GetGenericTypeDefinition() == typeof(IApprovalDeciders<>) || g.Key.GetGenericTypeDefinition() == typeof(IApprovalHandler<>));
        foreach (var group in counts)
        {
            var distinct = group.Select(d => d.ImplementationType ?? d.ImplementationInstance?.GetType() ?? d.ImplementationFactory?.Method.ReturnType).Distinct().Count();
            if (distinct > 1)
                problems.Add($"{Describe(group.Key)} has {distinct} implementations; a kind has one.");
        }

        var names = services
            .Select(d => d.ServiceType)
            .Where(t => t.IsGenericType && !t.IsGenericTypeDefinition && t.GetGenericTypeDefinition() == typeof(IApprovalDeciders<>))
            .Select(t => t.GetGenericArguments()[0])
            .Distinct()
            .GroupBy(NameOf, StringComparer.Ordinal)
            .Where(g => g.Count() > 1);
        foreach (var clash in names)
            problems.Add($"The approval kind name '{clash.Key}' is used by {string.Join(", ", clash.Select(t => t.FullName))}.");

        foreach (var handler in services.Select(d => d.ServiceType)
            .Where(t => t.IsGenericType && !t.IsGenericTypeDefinition && t.GetGenericTypeDefinition() == typeof(IApprovalHandler<>))
            .Distinct())
        {
            var deciders = typeof(IApprovalDeciders<>).MakeGenericType(handler.GetGenericArguments()[0]);
            if (!services.Any(d => d.ServiceType == deciders))
                problems.Add($"{Describe(handler)} is registered without {Describe(deciders)}: nobody could decide.");
        }

        foreach (var deciders in services.Select(d => d.ServiceType)
            .Where(t => t.IsGenericType && !t.IsGenericTypeDefinition && t.GetGenericTypeDefinition() == typeof(IApprovalDeciders<>))
            .Distinct())
        {
            var kind = deciders.GetGenericArguments()[0];
            if (kind == typeof(DocumentProposalApproval))
                continue;
            var handler = typeof(IApprovalHandler<>).MakeGenericType(kind);
            if (!services.Any(d => d.ServiceType == handler))
                problems.Add($"{Describe(deciders)} is registered without {Describe(handler)}: a decision would do nothing.");
        }
        return problems;
    }

    private static IReadOnlyDictionary<string, IApprovalKindRuntime> Scan(IServiceCollection services)
        => services
            .Select(d => d.ServiceType)
            .Where(t => t.IsGenericType && !t.IsGenericTypeDefinition && t.GetGenericTypeDefinition() == typeof(IApprovalDeciders<>))
            .Select(t => t.GetGenericArguments()[0])
            .Distinct()
            .Select(kind => (IApprovalKindRuntime)Activator.CreateInstance(typeof(ApprovalKindRuntime<>).MakeGenericType(kind))!)
            .GroupBy(k => k.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    internal static string NameOf(Type kind)
        => (string)typeof(ApprovalKindCatalog).GetMethod(nameof(NameOfKind), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(kind).Invoke(null, null)!;

    private static string NameOfKind<TKind>() where TKind : IApprovalKind => TKind.Name;

    private static string Describe(Type type) => $"{type.Name.Split('`')[0]}<{type.GetGenericArguments()[0].FullName}>";
}

/// <summary>One kind's deciders and handler, behind a non-generic face for the engine.</summary>
internal interface IApprovalKindRuntime
{
    string Name { get; }

    Type KindType { get; }

    ValueTask<bool> MayDecideAsync(IServiceProvider services, ApprovalDeciderContext context);

    ValueTask<IReadOnlyCollection<string>> GetDeciderIdsAsync(IServiceProvider services, ApprovalSubject subject, IAsyncDocumentSession session, CancellationToken cancellationToken);

    ValueTask<ApprovalScope> DecidesAllAsync(IServiceProvider services, ClaimsPrincipal user, CancellationToken cancellationToken);

    /// <summary>Runs the handler method for <see cref="ApprovalDecisionContext.Status"/>; false when the kind has no handler.</summary>
    Task<bool> RunAsync(IServiceProvider services, ApprovalDecisionContext context);
}

internal sealed class ApprovalKindRuntime<TKind> : IApprovalKindRuntime where TKind : IApprovalKind
{
    public string Name => TKind.Name;

    public Type KindType => typeof(TKind);

    public ValueTask<bool> MayDecideAsync(IServiceProvider services, ApprovalDeciderContext context)
        => services.GetRequiredService<IApprovalDeciders<TKind>>().MayDecideAsync(context);

    public ValueTask<IReadOnlyCollection<string>> GetDeciderIdsAsync(IServiceProvider services, ApprovalSubject subject, IAsyncDocumentSession session, CancellationToken cancellationToken)
        => services.GetRequiredService<IApprovalDeciders<TKind>>().GetDeciderIdsAsync(subject, session, cancellationToken);

    public ValueTask<ApprovalScope> DecidesAllAsync(IServiceProvider services, ClaimsPrincipal user, CancellationToken cancellationToken)
        => services.GetRequiredService<IApprovalDeciders<TKind>>().DecidesAllAsync(user, cancellationToken);

    public async Task<bool> RunAsync(IServiceProvider services, ApprovalDecisionContext context)
    {
        if (services.GetService<IApprovalHandler<TKind>>() is not { } handler)
            return false;
        var run = context.Status switch
        {
            ApprovalStatuses.Approved => handler.ApproveAsync(context),
            ApprovalStatuses.Rejected => handler.RejectAsync(context),
            ApprovalStatuses.Withdrawn => handler.WithdrawAsync(context),
            ApprovalStatuses.Expired => handler.ExpireAsync(context),
            ApprovalStatuses.Revoked => handler.RevokeAsync(context),
            _ => throw new ArgumentException($"No handler method for status '{context.Status}'.", nameof(context)),
        };
        await run;
        return true;
    }
}
