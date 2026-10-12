using Microsoft.AspNetCore.Http;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Actions;
using Raven.Client.Documents;
using Raven.Client.Exceptions;

namespace MintPlayer.Spark.Settings.Quotas;

/// <summary>
/// Per-user quotas (<c>docs/seo_analyzer_PRD.md</c> X3). <c>[SparkQuota]</c> on a custom action uses it for you;
/// call it yourself where a use is decided in code (e.g. "starting an audit counts, joining one does not").
/// </summary>
public interface ISparkQuotas
{
    /// <summary>Counts one use of <paramref name="quota"/> for <paramref name="key"/> if the window has room.</summary>
    /// <param name="key">Whose use: a user id, or anything stable (<see cref="KeyFor"/> gives the default).</param>
    Task<SparkQuotaDecision> TryConsumeAsync(SparkQuota quota, string key, CancellationToken cancellationToken = default);

    /// <summary>As <see cref="TryConsumeAsync"/>, throwing <see cref="SparkThrottledException"/> (429, <c>Retry-After</c>) when refused.</summary>
    Task ConsumeAsync(SparkQuota quota, string key, CancellationToken cancellationToken = default);

    /// <summary>Uses counted in the current window, without counting one.</summary>
    Task<int> GetUsedAsync(SparkQuota quota, string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// The default key of a caller: the signed-in user's id, else <c>ip:{address}</c>. The id is the cookie's
    /// <c>NameIdentifier</c>, else <c>ISparkCurrentUser.Id</c>, which reads an AI agent's bearer <c>sub</c> (MCP PRD D4):
    /// a user's agent spends the user's quota, never a separate one per address.
    /// </summary>
    static string KeyFor(HttpContext context)
        => UserIdOf(context) is { Length: > 0 } userId
            ? userId
            : "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");

    private static string? UserIdOf(HttpContext context)
    {
        if (context.User.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } id)
            return id;
        return context.User.Identity?.IsAuthenticated == true
            ? context.RequestServices?.GetService<ISparkCurrentUser>()?.Id
            : null;
    }
}

internal sealed class SparkQuotas(IDocumentStore store, ISparkSettings settings, SparkQuotaCatalog catalog, TimeProvider timeProvider, ILogger<SparkQuotas> logger) : ISparkQuotas
{
    private const int Attempts = 8;

    public async Task<SparkQuotaDecision> TryConsumeAsync(SparkQuota quota, string key, CancellationToken cancellationToken = default)
    {
        catalog.Require(quota);
        var (limit, window) = await LimitsAsync(quota, cancellationToken);
        if (limit < 0)
            return new SparkQuotaDecision(true, limit, int.MaxValue, null);

        var id = SparkQuotaUsage.IdFor(quota.Name, key);
        for (var attempt = 1; ; attempt++)
        {
            using var session = store.OpenAsyncSession();
            // Two concurrent uses must not both see the last free slot: the write fails if the document changed
            // since it was read (or appeared, for a new one), and the use is decided again.
            session.Advanced.OptimisticConcurrencyMode = Raven.Client.Documents.Session.OptimisticConcurrencyMode.Writes;
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var usage = await session.LoadAsync<SparkQuotaUsage>(id, cancellationToken);
            var isNew = usage is null;
            usage ??= new SparkQuotaUsage { Id = id };
            usage.Uses = usage.Uses.Where(u => u > now - window).OrderBy(u => u).ToList();

            if (usage.Uses.Count >= limit)
            {
                var retryAfter = usage.Uses.Count == 0 ? window : usage.Uses[usage.Uses.Count - limit] + window - now;
                return new SparkQuotaDecision(false, limit, 0, retryAfter < TimeSpan.Zero ? TimeSpan.Zero : retryAfter);
            }

            usage.Uses.Add(now);
            usage.ExpiresAtUtc = now + window;
            if (isNew)
                await session.StoreAsync(usage, string.Empty, id, cancellationToken);
            try
            {
                await session.SaveChangesAsync(cancellationToken);
                return new SparkQuotaDecision(true, limit, limit - usage.Uses.Count, null);
            }
            catch (ConcurrencyException) when (attempt < Attempts)
            {
                logger.LogDebug("Quota {Quota} for {Key}: concurrent use, deciding again (attempt {Attempt}).", quota.Name, key, attempt);
            }
        }
    }

    public async Task ConsumeAsync(SparkQuota quota, string key, CancellationToken cancellationToken = default)
    {
        var decision = await TryConsumeAsync(quota, key, cancellationToken);
        if (!decision.Allowed)
        {
            var (_, window) = await LimitsAsync(quota, cancellationToken);
            throw new SparkThrottledException($"Quota '{quota.Name}' reached: {decision.Limit} per {Describe(window)}.", decision.RetryAfter);
        }
    }

    public async Task<int> GetUsedAsync(SparkQuota quota, string key, CancellationToken cancellationToken = default)
    {
        catalog.Require(quota);
        var (_, window) = await LimitsAsync(quota, cancellationToken);
        using var session = store.OpenAsyncSession();
        var usage = await session.LoadAsync<SparkQuotaUsage>(SparkQuotaUsage.IdFor(quota.Name, key), cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return usage?.Uses.Count(u => u > now - window) ?? 0;
    }

    private async Task<(int Limit, TimeSpan Window)> LimitsAsync(SparkQuota quota, CancellationToken cancellationToken)
    {
        var limit = await settings.GetAsync(quota.Limit, cancellationToken);
        var window = await settings.GetAsync(quota.Window, cancellationToken);
        if (window <= TimeSpan.Zero)
        {
            // A setting the save validation cannot know is wrong; refuse rather than count forever or never.
            logger.LogWarning("Quota {Quota}: window {Window} is not positive; the declared default {Default} applies.", quota.Name, window, quota.Window.Default);
            window = quota.Window.Default;
        }
        return (limit, window);
    }

    private static string Describe(TimeSpan window)
        => window.TotalDays >= 1 && window.TotalDays == Math.Floor(window.TotalDays) ? (window.TotalDays == 1 ? "day" : $"{window.TotalDays:0} days")
         : window.TotalHours >= 1 && window.TotalHours == Math.Floor(window.TotalHours) ? (window.TotalHours == 1 ? "hour" : $"{window.TotalHours:0} hours")
         : window.ToString();
}

/// <summary>The declared quotas by name.</summary>
internal sealed class SparkQuotaCatalog
{
    private readonly Dictionary<string, SparkQuota> quotas = new(StringComparer.OrdinalIgnoreCase);

    public SparkQuota? Find(string name) => quotas.GetValueOrDefault(name);

    public IReadOnlyCollection<SparkQuota> All => quotas.Values;

    public void Add(SparkQuota quota)
    {
        if (!SparkQuota.IsValidName(quota.Name))
            throw new InvalidOperationException($"Quota name '{quota.Name}' is not valid: letters, digits, '.', '_' and '-', at most 64.");
        if (quotas.TryGetValue(quota.Name, out var existing) && existing != quota)
            throw new InvalidOperationException($"Quota '{quota.Name}' is declared twice, differently.");
        quotas[quota.Name] = quota;
    }

    public void AddFrom(Type type)
    {
        var found = type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.FieldType == typeof(SparkQuota)).Select(f => (SparkQuota?)f.GetValue(null))
            .Concat(type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Where(p => p.PropertyType == typeof(SparkQuota)).Select(p => (SparkQuota?)p.GetValue(null)))
            .OfType<SparkQuota>()
            .ToList();
        if (found.Count == 0)
            throw new InvalidOperationException($"{type.FullName} declares no public static SparkQuota.");
        foreach (var quota in found)
            Add(quota);
    }

    public void Require(SparkQuota quota)
    {
        if (Find(quota.Name) != quota)
            throw new InvalidOperationException($"Quota '{quota.Name}' was not declared through AddQuotas.");
    }
}

/// <summary>Applies <see cref="SparkQuotaAttribute"/> to custom actions, after every authorization gate.</summary>
internal sealed class SparkQuotaGate(ISparkQuotas quotas, SparkQuotaCatalog catalog) : ISparkCustomActionGate
{
    public async ValueTask BeforeExecuteAsync(SparkCustomActionGateContext context, CancellationToken cancellationToken)
    {
        foreach (var attribute in context.ActionType.GetCustomAttributes(typeof(SparkQuotaAttribute), inherit: true).Cast<SparkQuotaAttribute>())
        {
            var quota = catalog.Find(attribute.Name)
                ?? throw new InvalidOperationException($"[SparkQuota(\"{attribute.Name}\")] on {context.ActionType.Name}: no such quota was declared through AddQuotas.");
            await quotas.ConsumeAsync(quota, ISparkQuotas.KeyFor(context.HttpContext), cancellationToken);
        }
    }
}

/// <summary>Startup: every <c>[SparkQuota]</c> on a custom action names a declared quota, so a typo is a refused start, not a 500 per click.</summary>
internal static class SparkQuotaStartup
{
    public static void VerifyAttributes(SparkQuotaCatalog catalog)
    {
        var unknown = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && a.GetReferencedAssemblies().Any(r => r.Name == typeof(SparkQuotaAttribute).Assembly.GetName().Name))
            .SelectMany(a => { try { return a.GetTypes(); } catch (System.Reflection.ReflectionTypeLoadException ex) { return ex.Types.OfType<Type>().ToArray(); } })
            .Where(t => typeof(ICustomAction).IsAssignableFrom(t))
            .SelectMany(t => t.GetCustomAttributes(typeof(SparkQuotaAttribute), inherit: true).Cast<SparkQuotaAttribute>().Select(a => (Type: t, a.Name)))
            .Where(x => catalog.Find(x.Name) is null)
            .Select(x => $"{x.Type.FullName}: [SparkQuota(\"{x.Name}\")]")
            .ToList();
        if (unknown.Count > 0)
            throw new InvalidOperationException("Custom actions name quotas that no AddQuotas call declared: " + string.Join("; ", unknown));
    }
}
