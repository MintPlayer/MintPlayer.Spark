using MintPlayer.CliGenerator.Attributes;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Cli.Services;
using Spectre.Console;

namespace MintPlayer.Spark.Cli.Commands;

[CliCommand("webhooks", Description = "Work with a Spark site's webhooks: generate typed receivers from its event catalog.")]
[CliParentCommand(typeof(SparkCommand))]
public partial class WebhooksCommand : ICliCommand
{
    public Task<int> Execute(CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("spark webhooks generate --catalog <url|file> --lang cs|ts [[--name N]] [[--namespace NS]] [[-o file]]");
        AnsiConsole.MarkupLine("  The catalog is the site's GET /spark/apps/webhooks/catalog (OpenAPI 3.1 webhooks).");
        return Task.FromResult(1);
    }
}

[CliCommand("generate", Description = "Typed event classes and a processor (C#) or event types (TypeScript) from a site's webhook catalog.")]
[CliParentCommand(typeof(WebhooksCommand))]
public partial class WebhooksGenerateCommand : ICliCommand
{
    [Inject] private readonly WebhookGenerator generator;

    [CliOption("--catalog", Required = true, Description = "The catalog: an https URL (e.g. https://site/spark/apps/webhooks/catalog) or a JSON file."), NoInterfaceMember]
    public string Catalog { get; set; } = "";

    [CliOption("--lang", Required = true, Description = "cs (MintPlayer.Webhooks.Receiver) or ts (@mintplayer/webhooks-receiver)."), NoInterfaceMember]
    public string Lang { get; set; } = "";

    [CliOption("--name", Description = "The processor's base name: Coverage gives CoverageWebhooksProcessor. Default: from the catalog's title."), NoInterfaceMember]
    public string? Name { get; set; }

    [CliOption("--namespace", Description = "C#: the namespace. Default: Webhooks."), NoInterfaceMember]
    public string? Namespace { get; set; }

    [CliOption("--output", "-o", Description = "The file to write. Default: the standard output."), NoInterfaceMember]
    public string? Output { get; set; }

    public Task<int> Execute(CancellationToken cancellationToken)
        => generator.GenerateAsync(new WebhookGenerateOptions(Catalog, Lang, Name, Namespace, Output), cancellationToken);
}
