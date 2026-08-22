using System.Text;
using System.Text.Json;
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
    private readonly bool _wildcard;

    private SparkTestSecurity(bool wildcard, string? json = null)
    {
        _wildcard = wildcard;
        _json = json;
    }

    /// <summary>
    /// Everything granted, to everyone. The default, and what every endpoint test that is not
    /// about authorization wants: the endpoint's own logic under an "everyone can" baseline.
    /// </summary>
    /// <remarks>
    /// A wildcard grant to both well-known roles, so it is expressed the way an application would
    /// express it rather than by a switch that only tests have. That also means the permissive
    /// default is exercising the same evaluation path production does.
    /// </remarks>
    public static SparkTestSecurity Permissive => new(wildcard: true);

    /// <summary>
    /// Nothing granted to anyone. The deny-all mirror: what every Spark endpoint must do when the
    /// caller holds no right at all.
    /// </summary>
    public static SparkTestSecurity Empty => new(wildcard: false);

    /// <summary>Boots the host with this exact JSON, for a test about the file's own shape.</summary>
    public static SparkTestSecurity FromJson(string json) => new(wildcard: false, json);

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
    /// fixture happens to contain. Denials beat the wildcard, which is the tier order under test.
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

    /// <summary>The JSON this configuration writes.</summary>
    public string Build()
    {
        if (_json is not null)
            return _json;

        var rights = new List<Right>(_rights);

        if (_wildcard)
        {
            rights.Insert(0, new Right { Id = DeriveId("wildcard:anonymous"), Resource = "*/*", GroupId = AnonymousGroupId });
            rights.Insert(1, new Right { Id = DeriveId("wildcard:authenticated"), Resource = "*/*", GroupId = AuthenticatedGroupId });
        }

        foreach (var target in _withoutTargets.OrderBy(t => t, StringComparer.Ordinal))
        {
            rights.Add(new Right { Id = DeriveId("without:" + target), Resource = $"*/{target}", GroupId = AnonymousGroupId, IsDenied = true });
            rights.Add(new Right { Id = DeriveId("withoutauth:" + target), Resource = $"*/{target}", GroupId = AuthenticatedGroupId, IsDenied = true });
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
    /// Derives a right's id from what it is, never from <see cref="Guid.NewGuid"/>.
    /// </summary>
    /// <remarks>
    /// A random id would make every run write a different file: posture snapshots would churn, and
    /// the duplicate-id validator would be firing on randomness rather than on a real duplicate.
    /// </remarks>
    private static Guid DeriveId(string key)
        => new(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(key)));
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
