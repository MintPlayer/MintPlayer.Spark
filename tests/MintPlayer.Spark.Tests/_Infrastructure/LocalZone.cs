using Xunit.Sdk;

namespace MintPlayer.Spark.Tests._Infrastructure;

/// <summary>
/// Runs the classes in it after every parallel class has finished, and one at a time, so a test that
/// changes the process's local timezone through <see cref="LocalZone"/> cannot be observed by another.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LocalZoneCollection
{
    public const string Name = "LocalZone";
}

/// <summary>
/// Makes <see cref="TimeZoneInfo.Local"/> a non-UTC zone for the lifetime of the instance, so a defect
/// that reads an offset-less value as server-local can reproduce on a CI runner that is in UTC.
/// </summary>
/// <remarks>
/// <para>
/// On Linux .NET resolves the local zone from <c>TZ</c>, and <see cref="TimeZoneInfo.ClearCachedData"/>
/// makes it look again. Measured (PRD SP3, 2026-10-05): with the process started in <c>Etc/UTC</c>
/// the local zone becomes Brussels; parallel neighbours sampling the zone 784 times each never saw it
/// change, while a deliberately leaky control class was detected.
/// </para>
/// <para>
/// ⚠️ Windows ignores <c>TZ</c>. A test using this must therefore call <see cref="RequireNonUtc"/>,
/// which <em>fails</em> rather than skips: a Windows machine set to UTC, or an unknown zone id
/// (Linux silently falls back to UTC), would otherwise pass without testing anything.
/// </para>
/// <para>
/// Only for classes in <see cref="LocalZoneCollection"/>. Anything holding threads beyond its own class,
/// such as a host shared between classes, must be created and disposed inside that class.
/// </para>
/// </remarks>
public sealed class LocalZone : IDisposable
{
    private readonly string? _previous = Environment.GetEnvironmentVariable("TZ");

    public LocalZone(string tz = "Europe/Brussels")
    {
        Environment.SetEnvironmentVariable("TZ", tz);
        TimeZoneInfo.ClearCachedData();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TZ", _previous);
        TimeZoneInfo.ClearCachedData();
    }

    /// <summary>Fails the test when the local zone has no offset at <paramref name="instant"/>.</summary>
    public static void RequireNonUtc(DateTime instant)
    {
        if (TimeZoneInfo.Local.GetUtcOffset(instant) == TimeSpan.Zero)
            throw new XunitException(
                $"The local zone '{TimeZoneInfo.Local.Id}' has no offset at {instant:O}, so this test would " +
                "pass whether or not the code reads an offset-less value as local time. On Windows TZ is " +
                "ignored: run it on a machine whose zone is not UTC.");
    }
}
