using System.Reflection;
using MintPlayer.SourceGenerators.Attributes;
using Spectre.Console;

namespace MintPlayer.Spark.Cli.Services;

/// <summary>
/// Makes the installed MintPlayer.Spark.Templates match the tool before it instantiates anything (S3): the tool and the
/// template are released from the same commit at the same version, so the tool installs its own version with
/// <c>--force</c> when a different one (or none) is installed. A development build (no released version) uses what
/// is installed, or the nupkg passed as <c>--template</c>.
/// </summary>
public sealed partial class TemplateInstaller
{
    [Inject] private readonly ProcessRunner runner;

    /// <summary>The tool's own version, from the entry assembly (the tool host the versioning stamped).</summary>
    public static string ToolVersion
    {
        get
        {
            var version = (Assembly.GetEntryAssembly() ?? typeof(TemplateInstaller).Assembly)
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
            var plus = version.IndexOf('+');
            return plus < 0 ? version : version[..plus];
        }
    }

    private static bool IsReleased(string version) => !version.StartsWith("0.0.0", StringComparison.Ordinal);

    /// <summary>Installs or updates the template; returns false (after saying why) when it cannot.</summary>
    public async Task<bool> EnsureAsync(string? templatePackage, CancellationToken cancellationToken)
    {
        var workingDirectory = Directory.GetCurrentDirectory();
        if (templatePackage is not null)
        {
            var path = Path.GetFullPath(templatePackage);
            if (!File.Exists(path))
            {
                AnsiConsole.MarkupLineInterpolated($"[red]--template {path} does not exist.[/]");
                return false;
            }
            return await InstallAsync(path, workingDirectory, cancellationToken);
        }

        var listing = await runner.RunAsync("dotnet", ["new", "uninstall"], workingDirectory, capture: true, cancellationToken: cancellationToken);
        var installed = TemplateInstallation.ParseInstalledVersion(listing.Output);
        var wanted = ToolVersion;
        if (!IsReleased(wanted))
        {
            if (installed is not null)
            {
                AnsiConsole.MarkupLineInterpolated($"[grey]A development build of spark: using the installed {TemplateInstallation.PackageId} {installed}.[/]");
                return true;
            }
            AnsiConsole.MarkupLineInterpolated($"[red]A development build of spark has no template version to install; pass --template <path to {TemplateInstallation.PackageId}.nupkg>.[/]");
            return false;
        }
        if (installed == wanted) return true;

        AnsiConsole.MarkupLineInterpolated($"[grey]Installing {TemplateInstallation.PackageId} {wanted}{(installed is null ? "" : $" (replacing {installed})")}.[/]");
        return await InstallAsync($"{TemplateInstallation.PackageId}::{wanted}", workingDirectory, cancellationToken);
    }

    private async Task<bool> InstallAsync(string package, string workingDirectory, CancellationToken cancellationToken)
    {
        // --force: reinstalling the same package otherwise fails (exit 106).
        var result = await runner.RunAsync("dotnet", ["new", "install", package, "--force"], workingDirectory, capture: true, cancellationToken: cancellationToken);
        if (result.ExitCode == 0) return true;
        AnsiConsole.MarkupLineInterpolated($"[red]dotnet new install {package} failed (exit {result.ExitCode}):[/]");
        AnsiConsole.WriteLine(result.Output);
        return false;
    }
}
