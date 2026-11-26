using MintPlayer.CliGenerator.Attributes;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Cli.Services;

namespace MintPlayer.Spark.Cli.Commands;

[CliCommand("new", Description = "Create a Spark app from the spark-app template. On a terminal it asks for whatever is not given.")]
[CliParentCommand(typeof(SparkCommand))]
public partial class NewCommand : ICliCommand
{
    [Inject] private readonly AppCreator creator;

    [CliArgument(0, Name = "name", Required = false, Description = "The app's name: the solution, the app project and its namespace."), NoInterfaceMember]
    public string? Name { get; set; }

    [CliOption("--output", "-o", Description = "The directory to create; defaults to the name."), NoInterfaceMember]
    public string? Output { get; set; }

    [CliOption("--store", Description = "RavenDB (default) or InMemory. Sign-in, Replication and the OpenSearch engine need RavenDB."), NoInterfaceMember]
    public string? Store { get; set; }

    [CliOption("--auth", Description = "none (default), authorization (local accounts) or oidc (an external identity provider)."), NoInterfaceMember]
    public string? Auth { get; set; }

    [CliOption("--oidc-authority", Description = "The identity provider's address, with --auth oidc."), NoInterfaceMember]
    public string? OidcAuthority { get; set; }

    [CliOption("--features", Description = "Comma-separated feature flags (e.g. migrations,history), or none. Prerequisites are added. Default: the template's defaults."), NoInterfaceMember]
    public string? Features { get; set; }

    [CliOption("--no-client", Description = "No Angular client: an API-only app."), NoInterfaceMember]
    public bool NoClient { get; set; }

    [CliOption("--yes", "-y", Description = "Ask nothing: take the defaults for what is not given."), NoInterfaceMember]
    public bool Yes { get; set; }

    [CliOption("--no-interactive", Description = "Ask nothing (as --yes)."), NoInterfaceMember]
    public bool NoInteractive { get; set; }

    [CliOption("--skip-npm", Description = "Do not run npm install in ClientApp."), NoInterfaceMember]
    public bool SkipNpm { get; set; }

    [CliOption("--skip-sync", Description = "Do not synchronize the model; the app does it on its first start."), NoInterfaceMember]
    public bool SkipSync { get; set; }

    [CliOption("--template", Hidden = true, Description = "A MintPlayer.Spark.Templates nupkg to install instead of the tool's own version."), NoInterfaceMember]
    public string? Template { get; set; }

    public Task<int> Execute(CancellationToken cancellationToken)
        => creator.CreateAsync(
            new NewOptions(Name, Output, Store, Auth, OidcAuthority, Features, NoClient, Yes, NoInteractive, SkipNpm, SkipSync, Template),
            cancellationToken);
}
