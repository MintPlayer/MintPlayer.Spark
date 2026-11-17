using MintPlayer.CliGenerator.Attributes;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Cli.Services;
using Spectre.Console;

namespace MintPlayer.Spark.Cli.Commands;

// One subcommand per item, so each shows only its own options in --help. It also keeps an item's name from being
// parsed as an option: CliGenerator 13.1.1 names an option after its alias without the dashes, and System.CommandLine
// 2.0 matches an option's name as a token, so a bare `entity` was read as `--entity` (`spark add entity Invoice`
// failed with "Required argument missing", plan M5).

[CliCommand("add", Description = "Add to this Spark app: entity, actions, custom-action, lookup or index.")]
[CliParentCommand(typeof(SparkCommand))]
public partial class AddCommand : ICliCommand
{
    public Task<int> Execute(CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("spark add <item> <name>; items:");
        AnsiConsole.MarkupLine("  entity <Name>                    Entities/<Name>.cs, its context root and security right");
        AnsiConsole.MarkupLine("  actions <Entity>                 Actions/<Entity>Actions.cs");
        AnsiConsole.MarkupLine("  custom-action <Name> --entity E  the class, its actions.json entry and its right");
        AnsiConsole.MarkupLine("  lookup <Name>                    LookupReferences/<Name>.cs");
        AnsiConsole.MarkupLine("  index <Entity>                   Indexes/<Plural>_Overview.cs and its view");
        AnsiConsole.MarkupLine("spark add <item> --help shows the item's options.");
        return Task.FromResult(1);
    }
}

[CliCommand("entity", Description = "An entity class (in the entity library), its context root and its security right; then synchronizes the model.")]
[CliParentCommand(typeof(AddCommand))]
public partial class AddEntityCommand : ICliCommand
{
    [Inject] private readonly ItemAdder adder;

    [CliArgument(0, Name = "name", Description = "The entity's class name."), NoInterfaceMember]
    public string Name { get; set; } = "";

    [CliOption("--plural", Description = "The context property; defaults to the name with an 's'."), NoInterfaceMember]
    public string? Plural { get; set; }

    [CliOption("--group", Description = "The group granted it: a group name from security.json, its id, or @authenticated/@anonymous. Default: Administrators."), NoInterfaceMember]
    public string? Group { get; set; }

    [CliOption("--namespace", Description = "The library's root namespace; defaults to the project's."), NoInterfaceMember]
    public string? Namespace { get; set; }

    [CliOption("--skip-sync", Description = "Do not run --spark-synchronize-model afterwards."), NoInterfaceMember]
    public bool SkipSync { get; set; }

    [CliOption("--template", Hidden = true, Description = "A MintPlayer.Spark.Templates nupkg to install instead of the tool's own version."), NoInterfaceMember]
    public string? Template { get; set; }

    public Task<int> Execute(CancellationToken cancellationToken)
        => adder.AddAsync(new AddOptions("entity", Name, null, Plural, Group, Namespace, SkipSync, Template), cancellationToken);
}

[CliCommand("actions", Description = "An actions class for an entity (in the app): its hooks and the rows a caller sees.")]
[CliParentCommand(typeof(AddCommand))]
public partial class AddActionsCommand : ICliCommand
{
    [Inject] private readonly ItemAdder adder;

    [CliArgument(0, Name = "entity", Description = "The entity's class name."), NoInterfaceMember]
    public string Name { get; set; } = "";

    [CliOption("--namespace", Description = "The app's root namespace; defaults to the project's."), NoInterfaceMember]
    public string? Namespace { get; set; }

    [CliOption("--template", Hidden = true, Description = "A MintPlayer.Spark.Templates nupkg to install instead of the tool's own version."), NoInterfaceMember]
    public string? Template { get; set; }

    public Task<int> Execute(CancellationToken cancellationToken)
        => adder.AddAsync(new AddOptions("actions", Name, null, null, null, Namespace, SkipSync: true, Template), cancellationToken);
}

[CliCommand("custom-action", Description = "A custom action (in the app), its actions.json entry and its security right; then synchronizes the model.")]
[CliParentCommand(typeof(AddCommand))]
public partial class AddCustomActionCommand : ICliCommand
{
    [Inject] private readonly ItemAdder adder;

    [CliArgument(0, Name = "name", Description = "The action's class name, e.g. Approve."), NoInterfaceMember]
    public string Name { get; set; } = "";

    [CliOption("--entity", Required = true, Description = "The entity it acts on."), NoInterfaceMember]
    public string Entity { get; set; } = "";

    [CliOption("--group", Description = "The group granted it: a group name from security.json, its id, or @authenticated/@anonymous. Default: Administrators."), NoInterfaceMember]
    public string? Group { get; set; }

    [CliOption("--namespace", Description = "The app's root namespace; defaults to the project's."), NoInterfaceMember]
    public string? Namespace { get; set; }

    [CliOption("--skip-sync", Description = "Do not run --spark-synchronize-model afterwards."), NoInterfaceMember]
    public bool SkipSync { get; set; }

    [CliOption("--template", Hidden = true, Description = "A MintPlayer.Spark.Templates nupkg to install instead of the tool's own version."), NoInterfaceMember]
    public string? Template { get; set; }

    public Task<int> Execute(CancellationToken cancellationToken)
        => adder.AddAsync(new AddOptions("custom-action", Name, Entity, null, Group, Namespace, SkipSync, Template), cancellationToken);
}

[CliCommand("lookup", Description = "A lookup reference (in the entity library); then synchronizes the model.")]
[CliParentCommand(typeof(AddCommand))]
public partial class AddLookupCommand : ICliCommand
{
    [Inject] private readonly ItemAdder adder;

    [CliArgument(0, Name = "name", Description = "The lookup's class name, e.g. InvoiceStatus."), NoInterfaceMember]
    public string Name { get; set; } = "";

    [CliOption("--namespace", Description = "The library's root namespace; defaults to the project's."), NoInterfaceMember]
    public string? Namespace { get; set; }

    [CliOption("--skip-sync", Description = "Do not run --spark-synchronize-model afterwards."), NoInterfaceMember]
    public bool SkipSync { get; set; }

    [CliOption("--template", Hidden = true, Description = "A MintPlayer.Spark.Templates nupkg to install instead of the tool's own version."), NoInterfaceMember]
    public string? Template { get; set; }

    public Task<int> Execute(CancellationToken cancellationToken)
        => adder.AddAsync(new AddOptions("lookup", Name, null, null, null, Namespace, SkipSync, Template), cancellationToken);
}

[CliCommand("index", Description = "An overview index and its view for an entity (in the app); then synchronizes the model.")]
[CliParentCommand(typeof(AddCommand))]
public partial class AddIndexCommand : ICliCommand
{
    [Inject] private readonly ItemAdder adder;

    [CliArgument(0, Name = "entity", Description = "The entity's class name."), NoInterfaceMember]
    public string Name { get; set; } = "";

    [CliOption("--plural", Description = "The index name's plural; defaults to the entity with an 's'."), NoInterfaceMember]
    public string? Plural { get; set; }

    [CliOption("--namespace", Description = "The app's root namespace; defaults to the project's."), NoInterfaceMember]
    public string? Namespace { get; set; }

    [CliOption("--skip-sync", Description = "Do not run --spark-synchronize-model afterwards."), NoInterfaceMember]
    public bool SkipSync { get; set; }

    [CliOption("--template", Hidden = true, Description = "A MintPlayer.Spark.Templates nupkg to install instead of the tool's own version."), NoInterfaceMember]
    public string? Template { get; set; }

    public Task<int> Execute(CancellationToken cancellationToken)
        => adder.AddAsync(new AddOptions("index", Name, null, Plural, null, Namespace, SkipSync, Template), cancellationToken);
}
