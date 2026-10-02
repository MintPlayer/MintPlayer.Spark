using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;

namespace MintPlayer.Spark.Testing;

/// <summary>
/// The <c>security.json</c> a test host boots with.
/// </summary>
/// <remarks>
/// The file is the normal override seam. Authorization is not optional any more, so a host that
/// writes no file does not start — and a test that wants to exercise a rights model should say so
/// in the same language the application does, rather than by swapping a service.
/// <para>
/// For the two cases a grant list cannot express — "record what was asked" and "decide by
/// predicate" — swap <see cref="IAccessControl"/> wholesale through the factory's
/// <c>configureServices</c> hook, which still runs last. See <see cref="SparkTestAccessControl"/>.
/// </para>
/// </remarks>
public sealed class SparkTestSecurity
{
    private readonly string? _json;
    private readonly List<Right> _rights = [];
    private readonly HashSet<string> _withoutTargets = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _permissive;

    private SparkTestSecurity(bool permissive, string? json = null)
    {
        _permissive = permissive;
        _json = json;
    }

    /// <summary>
    /// Everything granted, to everyone. The default, and what every endpoint test that is not
    /// about authorization wants: the endpoint's own logic under an "everyone can" baseline.
    /// </summary>
    /// <remarks>
    /// This used to be a <c>*/*</c> grant in the file. Wildcard rights were removed (#460, D3) —
    /// the runtime refuses them — and "everything" cannot be enumerated by a builder that does not
    /// know the fixture's custom actions or controller resources. So the baseline is now a thin
    /// layer over the real evaluator, added by <see cref="SparkEndpointFactory{TContext}"/>: a
    /// resource is allowed when <c>security.json</c> allows it <em>or</em> when nothing this
    /// builder denied covers it. Denials (<see cref="Denying"/>, <see cref="Without"/>) are still
    /// written to the file and still evaluated by the production code path.
    /// <para>
    /// ⚠️ A host assembled by hand through <see cref="SparkTestSecurityFile.Write"/> gets only the
    /// file, which grants nothing — so there <c>Permissive</c> means "boots", not "allows".
    /// </para>
    /// </remarks>
    public static SparkTestSecurity Permissive => new(permissive: true);

    /// <summary>
    /// Nothing granted to anyone. The deny-all mirror: what every Spark endpoint must do when the
    /// caller holds no right at all.
    /// </summary>
    public static SparkTestSecurity Empty => new(permissive: false);

    /// <summary>Boots the host with this exact JSON, for a test about the file's own shape.</summary>
    public static SparkTestSecurity FromJson(string json) => new(permissive: false, json);

    /// <summary>Boots the host with the file at <paramref name="path"/>, copied in verbatim.</summary>
    public static SparkTestSecurity FromFile(string path) => FromJson(File.ReadAllText(path));

    /// <summary>
    /// Grants <paramref name="resource"/> — <c>{action}/{target}</c> — to both well-known roles.
    /// </summary>
    public SparkTestSecurity Granting(params string[] resources)
    {
        foreach (var resource in resources)
        {
            _rights.Add(new Right { Id = DeriveId("grant:" + resource), Resource = resource, GroupId = AnonymousGroupId });
            _rights.Add(new Right { Id = DeriveId("grantauth:" + resource), Resource = resource, GroupId = AuthenticatedGroupId });
        }

        return this;
    }

    /// <summary>Denies <paramref name="resource"/> to both well-known roles.</summary>
    public SparkTestSecurity Denying(params string[] resources)
    {
        foreach (var resource in resources)
        {
            _rights.Add(new Right { Id = DeriveId("deny:" + resource), Resource = resource, GroupId = AnonymousGroupId, IsDenied = true });
            _rights.Add(new Right { Id = DeriveId("denyauth:" + resource), Resource = resource, GroupId = AuthenticatedGroupId, IsDenied = true });
        }

        return this;
    }

    /// <summary>
    /// Permissive except for these targets — the shape most authorization tests want, where one
    /// type is off-limits and the rest of the fixture still works.
    /// </summary>
    /// <remarks>
    /// A denial rather than a narrowed grant, so the caller need not enumerate every type the
    /// fixture happens to contain. Every action on the target is withheld, custom ones included;
    /// the file carries the five built-in actions as a concrete combined denial.
    /// </remarks>
    public SparkTestSecurity Without(params string[] targets)
    {
        foreach (var target in targets)
            _withoutTargets.Add(target);

        return this;
    }

    /// <summary>
    /// The group ids the builder emits. Fixed and public so a test can grant to them directly, and
    /// so a test asserting on a decision can name the group it expects to have decided it.
    /// </summary>
    public static readonly Guid AnonymousGroupId = Guid.Parse("00000000-0000-0000-0000-0000000a0000");

    /// <inheritdoc cref="AnonymousGroupId"/>
    public static readonly Guid AuthenticatedGroupId = Guid.Parse("00000000-0000-0000-0000-0000000a0001");

    /// <summary>Whether this configuration carries the "everything not denied" baseline.</summary>
    internal bool IsPermissive => _permissive && _json is null;

    /// <summary>
    /// Whether a denial this builder emitted covers <paramref name="resource"/> — the half of the
    /// permissive baseline the file cannot express. Combined actions expand exactly as the
    /// evaluator expands them, because the check runs through <see cref="RightsDecision"/>.
    /// </summary>
    internal bool Denies(string resource)
    {
        if (_withoutTargets.Contains(ResourcePattern.Parse(resource).Target))
            return true;

        var denials = new SecurityConfiguration
        {
            Rights = _rights
                .Where(r => r.IsDenied && r.GroupId == AnonymousGroupId)
                .Select(r => new Right { Id = r.Id, Resource = r.Resource, GroupId = AnonymousGroupId })
                .ToList(),
        };

        return RightsDecision.For(denials, new HashSet<Guid> { AnonymousGroupId }).Allows(resource);
    }

    /// <summary>The JSON this configuration writes.</summary>
    public string Build()
    {
        if (_json is not null)
            return _json;

        var rights = new List<Right>(_rights);

        foreach (var target in _withoutTargets.OrderBy(t => t, StringComparer.Ordinal))
        {
            rights.Add(new Right { Id = DeriveId("without:" + target), Resource = $"QueryReadEditNewDelete/{target}", GroupId = AnonymousGroupId, IsDenied = true });
            rights.Add(new Right { Id = DeriveId("withoutauth:" + target), Resource = $"QueryReadEditNewDelete/{target}", GroupId = AuthenticatedGroupId, IsDenied = true });
        }

        var config = new SecurityConfiguration
        {
            // Emitted always, so a builder-produced file can never trip the well-known validators.
            WellKnown = new Dictionary<string, string>
            {
                [SparkWellKnownGroups.Anonymous] = AnonymousGroupId.ToString(),
                [SparkWellKnownGroups.Authenticated] = AuthenticatedGroupId.ToString(),
            },
            Groups = new Dictionary<string, TranslatedString>
            {
                [AnonymousGroupId.ToString()] = TranslatedString.Create("Anonymous visitors"),
                [AuthenticatedGroupId.ToString()] = TranslatedString.Create("Signed-in users"),
            },
            Rights = rights,
        };

        return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Layers the permissive baseline over the registered <see cref="IAccessControl"/>, when this
    /// configuration is <see cref="Permissive"/>. The inner service is the production evaluator,
    /// constructed from its own registration, so an important denial in the file still refuses.
    /// </summary>
    internal void ApplyBaseline(IServiceCollection services)
    {
        if (!IsPermissive)
            return;

        var inner = services.LastOrDefault(d => d.ServiceType == typeof(IAccessControl));
        if (inner?.ImplementationType is not { } innerType)
            return;

        services.Remove(inner);
        services.Add(new ServiceDescriptor(
            typeof(IAccessControl),
            sp => new PermissiveBaselineAccessControl(
                (IAccessControl)ActivatorUtilities.CreateInstance(sp, innerType), this),
            inner.Lifetime));
    }

    /// <summary>
    /// Derives a right's id from what it is, never from <see cref="Guid.NewGuid"/>.
    /// </summary>
    /// <remarks>
    /// A random id would make every run write a different file: posture snapshots would churn, and
    /// the duplicate-id validator would be firing on randomness rather than on a real duplicate.
    /// </remarks>
    private static Guid DeriveId(string key)
        => new(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(key)));

    /// <summary>
    /// Allowed when the file allows it, or when no denial of the builder's covers it. The file is
    /// asked first so that an important denial — which the file alone can express — still wins.
    /// </summary>
    private sealed class PermissiveBaselineAccessControl(IAccessControl inner, SparkTestSecurity security) : IAccessControl
    {
        public async Task<bool> IsAllowedAsync(string resource, CancellationToken cancellationToken = default)
            => await inner.IsAllowedAsync(resource, cancellationToken) || !security.Denies(resource);

        /// <summary>
        /// The file's attribute rights when the file grants the type; otherwise the baseline's type
        /// decision with every attribute inheriting it (the builder expresses no attribute rights).
        /// </summary>
        public async Task<EffectiveAttributeRights> GetAttributeRightsAsync(
            string verb, string entityTypeName, CancellationToken cancellationToken = default)
        {
            var fromFile = await inner.GetAttributeRightsAsync(verb, entityTypeName, cancellationToken);
            return fromFile.TypeAllowed
                ? fromFile
                : EffectiveAttributeRights.Inherit(entityTypeName, verb, await IsAllowedAsync($"{verb}/{entityTypeName}", cancellationToken));
        }
    }
}

/// <summary>
/// Writes a <c>security.json</c> into a content root that a test host built by hand owns.
/// </summary>
/// <remarks>
/// Symmetric with <c>WriteSparkModelHashes</c>, and needed for the same reason: a host assembled
/// without <see cref="SparkEndpointFactory{TContext}"/> still meets the startup gate.
/// </remarks>
public static class SparkTestSecurityFile
{
    /// <summary>Writes <paramref name="security"/> (permissive by default) into <paramref name="contentRootPath"/>.</summary>
    public static void Write(string contentRootPath, SparkTestSecurity? security = null)
    {
        var path = Path.Combine(contentRootPath, "App_Data", "security.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, (security ?? SparkTestSecurity.Permissive).Build());
    }
}
