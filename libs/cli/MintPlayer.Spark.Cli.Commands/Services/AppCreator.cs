using System.Text.RegularExpressions;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Templates.Build;
using Spectre.Console;

namespace MintPlayer.Spark.Cli.Services;

/// <summary>What <c>spark new</c> was given on the command line; whatever is missing is asked for, or defaulted.</summary>
public sealed record NewOptions(
    string? Name,
    string? Output,
    string? Store,
    string? Auth,
    string? OidcAuthority,
    string? Features,
    bool NoClient,
    bool Yes,
    bool NoInteractive,
    bool SkipNpm,
    bool SkipSync,
    string? Template);

/// <summary>
/// <c>spark new</c>: asks for what is missing, refuses what the template cannot do, checks RavenDB and the OIDC
/// authority, instantiates spark-app, installs the client's packages and synchronizes the model once, so the first
/// <c>dotnet run</c> starts a working app (docs/templates_dnx_terminal_PRD.md §6).
/// </summary>
public sealed partial class AppCreator
{
    [Inject] private readonly ProcessRunner runner;
    [Inject] private readonly TemplateInstaller installer;
    [Inject] private readonly Probes probes;
    [Inject] private readonly ModelSynchronizer synchronizer;

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$")]
    private static partial Regex ProjectName();

    /// <summary>Prompts only on a terminal a person is at: not with --yes or --no-interactive, piped input, or on CI.</summary>
    public static bool CanPrompt(bool yes, bool noInteractive)
        => !yes && !noInteractive && !Console.IsInputRedirected
           && Environment.GetEnvironmentVariable("CI") is not ("true" or "1" or "True");

    public async Task<int> CreateAsync(NewOptions options, CancellationToken cancellationToken)
    {
        var interactive = CanPrompt(options.Yes, options.NoInteractive);
        var manifest = Features.Manifest;

        var name = options.Name ?? (interactive
            ? AnsiConsole.Prompt(new TextPrompt<string>("App name:").DefaultValue("SparkApp")
                .Validate(n => ProjectName().IsMatch(n), "Use letters, digits, '_' and '.', starting with a letter."))
            : null);
        if (name is null) return Fail("Name the app: spark new <name>.");
        if (!ProjectName().IsMatch(name)) return Fail($"\"{name}\" is not a valid project name: use letters, digits, '_' and '.', starting with a letter.");

        var store = Choice(options.Store, NewRequest.Stores)
            ?? (interactive ? Select("Data store:", NewRequest.Stores) : NewRequest.RavenDb);
        var auth = Choice(options.Auth, NewRequest.AuthModes)
            ?? (interactive && store == NewRequest.RavenDb ? Select("Sign-in:", NewRequest.AuthModes) : "none");
        var authority = options.OidcAuthority
            ?? (interactive && auth == "oidc" ? AnsiConsole.Prompt(new TextPrompt<string>("OIDC authority (https://…):")) : null);

        var noClient = options.NoClient;
        IReadOnlyList<string>? features = options.Features is { } list
            ? list.Equals("none", StringComparison.OrdinalIgnoreCase) ? [] : list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : null;
        if (features is null && interactive)
        {
            if (!noClient) noClient = !AnsiConsole.Confirm("Angular client?", defaultValue: true);
            features = PromptFeatures(manifest, store, noClient);
        }

        var request = new NewRequest(name, options.Output ?? name, store, auth, authority, features, noClient);
        var errors = request.Validate(manifest);
        if (errors.Count > 0)
        {
            foreach (var error in errors) AnsiConsole.MarkupLineInterpolated($"[red]{error}[/]");
            return 2;
        }
        var added = request.AddedPrerequisites(manifest);
        if (added.Count > 0) AnsiConsole.MarkupLineInterpolated($"[grey]Also included, as prerequisites: {string.Join(", ", added)}.[/]");

        var solutionDirectory = Path.GetFullPath(request.Output);
        if (Directory.Exists(solutionDirectory) && Directory.EnumerateFileSystemEntries(solutionDirectory).Any())
            return Fail($"{solutionDirectory} is not empty.");

        var ravenUp = store != NewRequest.RavenDb || await probes.RavenDbReachableAsync(cancellationToken);
        if (!ravenUp && interactive && InMemoryAlternative(request, manifest) is var (inMemory, dropped)
            && AnsiConsole.Confirm(
                $"RavenDB is not answering at {Probes.RavenDbUrl}. Use the InMemory store instead{(dropped.Count > 0 ? $" (without {string.Join(", ", dropped)})" : "")}?",
                defaultValue: false))
        {
            request = inMemory;
            ravenUp = true;
        }
        else if (!ravenUp)
            AnsiConsole.MarkupLineInterpolated($"[yellow]RavenDB is not answering at {Probes.RavenDbUrl}. The app needs it to start (appsettings.json, Spark:RavenDb); the model is synchronized on its first run instead.[/]");
        if (auth == "oidc" && !await probes.OidcDiscoveryReachableAsync(authority!, cancellationToken))
            AnsiConsole.MarkupLineInterpolated($"[yellow]{authority} serves no OpenID configuration (/.well-known/openid-configuration) right now; sign-in fails until it does.[/]");

        if (!await installer.EnsureAsync(options.Template, cancellationToken)) return 1;

        var created = await runner.RunAsync("dotnet", request.TemplateArguments(manifest), Directory.GetCurrentDirectory(), cancellationToken: cancellationToken);
        if (created.ExitCode != 0) return created.ExitCode;

        var appDirectory = Path.Combine(solutionDirectory, name);
        var clientApp = Path.Combine(appDirectory, "ClientApp");
        if (Directory.Exists(clientApp) && !options.SkipNpm)
        {
            AnsiConsole.MarkupLine("[grey]npm install (ClientApp)…[/]");
            var npm = await runner.RunAsync("npm", ["install", "--no-audit", "--no-fund"], clientApp, cancellationToken: cancellationToken);
            if (npm.ExitCode != 0)
                AnsiConsole.MarkupLineInterpolated($"[yellow]npm install failed (exit {npm.ExitCode}); run it in {clientApp} before the first start.[/]");
        }

        if (!options.SkipSync && ravenUp)
            await synchronizer.RunAsync(Path.Combine(appDirectory, $"{name}.csproj"), cancellationToken);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLineInterpolated($"[green]Created {name}.[/] Next:");
        AnsiConsole.MarkupLineInterpolated($"  cd {request.Output}");
        AnsiConsole.MarkupLineInterpolated($"  dotnet run --project {name}");
        AnsiConsole.MarkupLine("  spark add entity <Name>   [grey](in the app: a model type, its context root and right)[/]");
        return 0;
    }

    /// <summary>
    /// The same app on the InMemory store, minus the features that need RavenDB; null when sign-in is on (it needs
    /// RavenDB), since an app without its sign-in is not the same app.
    /// </summary>
    private static (NewRequest Request, List<string> Dropped)? InMemoryAlternative(NewRequest request, FeatureManifest manifest)
    {
        if (request.Auth != "none") return null;
        var byFlag = manifest.Features.Where(f => f.Flag is not null).ToDictionary(f => f.Flag!, StringComparer.OrdinalIgnoreCase);
        var chosen = request.Features ?? manifest.Features.Where(f => f.HasParameter && f.Default && f.Flag is not null).Select(f => f.Flag!).ToList();
        var kept = chosen.Where(flag => byFlag.TryGetValue(flag, out var f) && f.Store is null).ToList();
        var alternative = request with { Store = NewRequest.InMemory, Features = kept };
        return alternative.Validate(manifest).Count == 0 ? (alternative, chosen.Except(kept).ToList()) : null;
    }

    /// <summary>Accepts any casing of a choice; anything else passes through for <see cref="NewRequest.Validate"/> to refuse.</summary>
    private static string? Choice(string? value, string[] choices)
        => value is null ? null : choices.FirstOrDefault(c => c.Equals(value, StringComparison.OrdinalIgnoreCase)) ?? value;

    private static string Select(string title, string[] choices)
        => AnsiConsole.Prompt(new SelectionPrompt<string>().Title(title).AddChoices(choices));

    private static List<string> PromptFeatures(FeatureManifest manifest, string store, bool noClient)
    {
        var available = manifest.Features
            .Where(f => f.HasParameter && f.Flag is not null)
            .Where(f => f.Store is null || f.Store == store)
            .Where(f => !f.NeedsClient || !noClient)
            .ToList();
        var prompt = new MultiSelectionPrompt<Feature>()
            .Title("Features [grey](space toggles, enter accepts; prerequisites are added for you)[/]:")
            .NotRequired()
            .PageSize(15)
            .UseConverter(f => $"{Markup.Escape(f.DisplayName ?? f.Id)} [grey]{Markup.Escape(f.Description ?? "")}[/]")
            .AddChoices(available);
        foreach (var feature in available.Where(f => f.Default)) prompt.Select(feature);
        return AnsiConsole.Prompt(prompt).Select(f => f.Flag!).ToList();
    }

    private static int Fail(string message)
    {
        AnsiConsole.MarkupLineInterpolated($"[red]{message}[/]");
        return 2;
    }
}
