using System.ComponentModel;
using System.Diagnostics;
using CodeCoverage.Versioning.Releases;
using CodeCoverage.Versioning.Versions;

namespace CodeCoverage.Versioning.Build;

/// <summary>The .NET SDK a checkout builds with (PRD D8): its prerelease label is the maturity of the platform it targets.</summary>
public static class DotnetSdk
{
	/// <summary>
	/// <c>dotnet --version</c> run in <paramref name="workingDirectory"/>, so the checkout's <c>global.json</c> picks the
	/// SDK as it did for the build. Null when there is no dotnet, the pinned SDK is missing, or the answer is not a version.
	/// </summary>
	public static async Task<string?> VersionAsync(string workingDirectory, CancellationToken cancellationToken = default)
	{
		var start = new ProcessStartInfo("dotnet")
		{
			WorkingDirectory = workingDirectory,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		start.ArgumentList.Add("--version");
		start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
		start.Environment["DOTNET_NOLOGO"] = "1";
		try
		{
			using var process = Process.Start(start);
			if (process is null) return null;
			var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
			_ = process.StandardError.ReadToEndAsync(cancellationToken);
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeout.CancelAfter(TimeSpan.FromSeconds(30));
			try
			{
				await process.WaitForExitAsync(timeout.Token);
			}
			catch (OperationCanceledException)
			{
				process.Kill(entireProcessTree: true);
				throw;
			}
			var version = (await output).Trim();
			return process.ExitCode == 0 && version.Length <= ReleaseManifest.MaxVersionLength && SemVer.TryParse(version, out _) ? version : null;
		}
		catch (Exception e) when (e is Win32Exception or InvalidOperationException or OperationCanceledException)
		{
			return null;
		}
	}
}
