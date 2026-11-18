using System.Diagnostics;
using System.Text;

namespace MintPlayer.Spark.Cli.Services;

public sealed record ProcessResult(int ExitCode, string Output);

/// <summary>
/// Runs the tools <c>spark</c> drives (<c>dotnet new</c>, <c>npm install</c>, the app's synchronizer). A streamed run
/// shares the console, so the user sees the tool's own output and prompts; a captured run returns it instead.
/// </summary>
public sealed class ProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        bool capture = false,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default)
    {
        var info = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = capture,
            RedirectStandardError = capture,
        };
        // npm is a .cmd script on Windows, which CreateProcess cannot start without the shell.
        if (OperatingSystem.IsWindows() && fileName == "npm")
        {
            info.FileName = "cmd.exe";
            info.ArgumentList.Add("/d");
            info.ArgumentList.Add("/c");
            info.ArgumentList.Add("npm");
        }
        else
        {
            info.FileName = fileName;
        }
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        foreach (var (name, value) in environment ?? new Dictionary<string, string>()) info.Environment[name] = value;

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var output = new StringBuilder();
        if (capture)
        {
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        return new ProcessResult(process.ExitCode, output.ToString());
    }
}
