using Microsoft.Extensions.Hosting;

namespace MintPlayer.Spark.Commands;

/// <summary>
/// What a Spark command reads from the application: the registrations <c>AddSpark</c> saw, the host environment, and,
/// on the built app, its service provider (#536 D34). The commands run after <c>Build()</c>, where the
/// <see cref="IServiceCollection"/> is no longer reachable, so <c>AddSpark</c> keeps it in
/// <see cref="SparkServiceRegistrations"/>.
/// </summary>
/// <param name="Provider">The app's provider (a command scope's), or null where a caller has none and the command
/// builds a throwaway one from <paramref name="Services"/>.</param>
internal readonly record struct SparkCommandHost(IServiceCollection Services, IHostEnvironment Environment, IServiceProvider? Provider = null);

/// <summary>The service collection <c>AddSpark</c> registered into, for commands that read registrations (D34).</summary>
internal sealed record SparkServiceRegistrations(IServiceCollection Services);
