using MintPlayer.CliGenerator.Attributes;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Cli.Services;
using Spectre.Console;

namespace MintPlayer.Spark.Cli.Commands;

/// <summary>
/// <c>spark</c> with no command: outside an app on a terminal it starts <c>spark new</c>'s prompts (so
/// <c>dnx create-spark-app</c> and a bare <c>spark</c> do the same); otherwise it says what the commands are.
/// </summary>
[CliRootCommand(Name = "spark", Description = "Create Spark apps and add model types, actions and indexes to them.")]
public partial class SparkCommand : ICliCommand
{
    [Inject] private readonly AppCreator creator;

    public async Task<int> Execute(CancellationToken cancellationToken)
    {
        SparkProject? project = null;
        try
        {
            project = SparkProject.Locate(Directory.GetCurrentDirectory());
        }
        catch (InvalidOperationException)
        {
            // A solution with several apps is still a Spark solution: show the commands.
            project = new SparkProject("", "", "", null);
        }

        if (project is null && AppCreator.CanPrompt(yes: false, noInteractive: false))
            return await creator.CreateAsync(new NewOptions(null, null, null, null, null, null, false, false, false, false, false, null), cancellationToken);

        AnsiConsole.MarkupLine($"spark {Markup.Escape(TemplateInstaller.ToolVersion)}");
        AnsiConsole.MarkupLine("  spark new [[name]]              create an app (prompts for what is not given)");
        AnsiConsole.MarkupLine($"  spark add <item> <name>        add to this app; items: {string.Join(", ", ItemAdder.ItemNames)}");
        AnsiConsole.MarkupLine("  spark webhooks generate         typed receivers from a site's webhook catalog");
        AnsiConsole.MarkupLine("  spark <command> --help        the command's options");
        return project is null ? 1 : 0;
    }
}
