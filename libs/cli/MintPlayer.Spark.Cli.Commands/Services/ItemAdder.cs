using System.Text.RegularExpressions;
using MintPlayer.SourceGenerators.Attributes;
using Spectre.Console;

namespace MintPlayer.Spark.Cli.Services;

/// <summary>What <c>spark add</c> was given.</summary>
public sealed record AddOptions(string Item, string Name, string? Entity, string? Plural, string? Group, string? Namespace, bool SkipSync, string? Template);

/// <summary>
/// <c>spark add</c>: runs an item template in the project it belongs in, then makes the edits its doc comment lists
/// (the context root, the security right, the actions.json entry) and synchronizes the model. Every edit is computed
/// before anything is written, so a refusal leaves the app as it was.
/// </summary>
public sealed partial class ItemAdder
{
    [Inject] private readonly ProcessRunner runner;
    [Inject] private readonly TemplateInstaller installer;
    [Inject] private readonly ModelSynchronizer synchronizer;

    private const string DefaultGroup = "Administrators";

    private sealed record Item(string ShortName, bool InLibrary, bool Synchronizes);

    private static readonly Dictionary<string, Item> Items = new(StringComparer.OrdinalIgnoreCase)
    {
        ["entity"] = new("spark-entity", InLibrary: true, Synchronizes: true),
        ["lookup"] = new("spark-lookup", InLibrary: true, Synchronizes: true),
        ["actions"] = new("spark-actions", InLibrary: false, Synchronizes: false),
        ["custom-action"] = new("spark-custom-action", InLibrary: false, Synchronizes: true),
        ["index"] = new("spark-index", InLibrary: false, Synchronizes: true),
    };

    public static IEnumerable<string> ItemNames => Items.Keys;

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex Identifier();

    [GeneratedRegex(@"<RootNamespace>\s*([^<\s]+)\s*</RootNamespace>")]
    private static partial Regex RootNamespace();

    public async Task<int> AddAsync(AddOptions options, CancellationToken cancellationToken)
    {
        if (!Items.TryGetValue(options.Item, out var item))
            return Fail($"There is no item \"{options.Item}\". The items: {string.Join(", ", Items.Keys)}.");
        if (!Identifier().IsMatch(options.Name)) return Fail($"\"{options.Name}\" is not a C# identifier.");
        if (options.Plural is { } p && !Identifier().IsMatch(p)) return Fail($"--plural \"{p}\" is not a C# identifier.");
        if (options.Item.Equals("custom-action", StringComparison.OrdinalIgnoreCase) && options.Entity is null)
            return Fail("A custom action acts on an entity: pass --entity <Entity>.");
        if (options.Entity is { } e && !Identifier().IsMatch(e)) return Fail($"--entity \"{e}\" is not a C# identifier.");

        SparkProject? project;
        try
        {
            project = SparkProject.Locate(Directory.GetCurrentDirectory());
        }
        catch (InvalidOperationException ex)
        {
            return Fail(ex.Message);
        }
        if (project is null) return Fail("This is not inside a Spark app (a project with App_Data, or a solution holding one).");

        // Compute every edit first.
        var writes = new Dictionary<string, string>();
        var notes = new List<string>();
        try
        {
            switch (options.Item.ToLowerInvariant())
            {
                case "entity":
                    PlanEntity(project, options, writes, notes);
                    break;
                case "custom-action":
                    PlanCustomAction(project, options, writes);
                    break;
            }
        }
        catch (InvalidOperationException ex)
        {
            return Fail(ex.Message);
        }

        if (!await installer.EnsureAsync(options.Template, cancellationToken)) return 1;

        var arguments = new List<string> { "new", item.ShortName, "--name", options.Name };
        if (options.Entity is not null) arguments.AddRange(["--entity", options.Entity]);
        if (options.Plural is not null) arguments.AddRange(["--plural", options.Plural]);
        if (options.Namespace is not null) arguments.AddRange(["--namespace", options.Namespace]);
        var directory = item.InLibrary ? project.LibraryDirectory : project.AppDirectory;
        var created = await runner.RunAsync("dotnet", arguments, directory, cancellationToken: cancellationToken);
        if (created.ExitCode != 0) return created.ExitCode;

        foreach (var (path, content) in writes)
        {
            File.WriteAllText(path, content);
            AnsiConsole.MarkupLineInterpolated($"[grey]Updated {Path.GetRelativePath(Directory.GetCurrentDirectory(), path)}.[/]");
        }
        foreach (var note in notes) AnsiConsole.MarkupLineInterpolated($"[yellow]{note}[/]");

        if (item.Synchronizes && !options.SkipSync && !await synchronizer.RunAsync(project.AppProject, cancellationToken))
            return 1;
        return 0;
    }

    private static void PlanEntity(SparkProject project, AddOptions options, Dictionary<string, string> writes, List<string> notes)
    {
        if (project.ContextFile is null) throw new InvalidOperationException($"{project.AppDirectory} has no class deriving from SparkContext.");
        var plural = options.Plural ?? options.Name + "s";

        var context = ContextEditor.AddRoot(File.ReadAllText(project.ContextFile), options.Name, plural);
        var entityNamespace = $"{options.Namespace ?? LibraryNamespace(project)}.Entities";
        if (!context.Contains($"using {entityNamespace};") && !File.ReadAllText(project.AppProject).Contains($"<Using Include=\"{entityNamespace}\""))
            context = $"using {entityNamespace};{JsonText.NewLine(context)}" + context;
        writes[project.ContextFile] = context;

        var security = ReadSecurity(project);
        var group = SecurityRights.ResolveGroup(security, options.Group ?? DefaultGroup);
        writes[project.SecurityFile] = SecurityRights.Add(security, options.Name.ToLowerInvariant(), $"QueryReadEditNewDelete/{options.Name}", group);
        if (group.StartsWith('@'))
            notes.Add($"{group} may now read every {options.Name}. Add an actions class that limits the rows a caller sees: spark add actions {options.Name}.");
    }

    private static void PlanCustomAction(SparkProject project, AddOptions options, Dictionary<string, string> writes)
    {
        var actions = File.Exists(project.ActionsFile) ? File.ReadAllText(project.ActionsFile) : "{}";
        if (JsonText.HasProperty(actions, options.Name))
            throw new InvalidOperationException($"actions.json already has a custom action named {options.Name}.");
        writes[project.ActionsFile] = JsonText.AddProperty(actions, options.Name, """{ "showedOn": "both", "selectionRule": ">=1", "refreshOnCompleted": true }""");

        var security = ReadSecurity(project);
        var group = SecurityRights.ResolveGroup(security, options.Group ?? DefaultGroup);
        writes[project.SecurityFile] = SecurityRights.Add(
            security, $"{options.Entity!.ToLowerInvariant()}-{options.Name.ToLowerInvariant()}", $"{options.Name}/{options.Entity}", group);
    }

    private static string ReadSecurity(SparkProject project)
        => File.Exists(project.SecurityFile)
            ? File.ReadAllText(project.SecurityFile)
            : throw new InvalidOperationException($"{project.SecurityFile} does not exist.");

    private static string LibraryNamespace(SparkProject project)
    {
        var libraryProject = Directory.EnumerateFiles(project.LibraryDirectory, "*.csproj").First();
        var match = RootNamespace().Match(File.ReadAllText(libraryProject));
        return match.Success ? match.Groups[1].Value : Path.GetFileNameWithoutExtension(libraryProject);
    }

    private static int Fail(string message)
    {
        AnsiConsole.MarkupLineInterpolated($"[red]{message}[/]");
        return 2;
    }
}
