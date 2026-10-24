using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeCoverage.Versioning.Git;

/// <summary>
/// Runs the git CLI against one repository (bare mirror on the server, the workflow's checkout in local mode). Every
/// call is an argument array, never a shell string (PRD D5), with prompts, pagers and the user's and system's
/// configuration switched off so a consumer's repository cannot steer what runs.
/// </summary>
public sealed partial class GitCli(string gitDirectory, TimeSpan? timeout = null)
{
	private readonly TimeSpan timeout = timeout ?? TimeSpan.FromMinutes(2);

	public string GitDirectory { get; } = gitDirectory;

	[GeneratedRegex(@"^[0-9a-f]{40}(?:[0-9a-f]{24})?\z", RegexOptions.CultureInvariant)]
	private static partial Regex ObjectIdGrammar();

	public static bool IsObjectId(string? text) => text is not null && ObjectIdGrammar().IsMatch(text);

	public static string RequireObjectId(string text)
		=> IsObjectId(text) ? text : throw new ArgumentException($"'{text}' is not a full commit sha.", nameof(text));

	public sealed record Result(int ExitCode, byte[] Output, string Error)
	{
		public string Text => Encoding.UTF8.GetString(Output);

		public Result EnsureSuccess(string what) => ExitCode == 0 ? this : throw new GitException($"git {what} failed ({ExitCode}): {Error.Trim()}");
	}

	public async Task<Result> RunAsync(IEnumerable<string> arguments, byte[]? input = null, IReadOnlyDictionary<string, string>? environment = null, CancellationToken cancellationToken = default)
	{
		var start = new ProcessStartInfo("git")
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		start.ArgumentList.Add("--git-dir=" + GitDirectory);
		foreach (var setting in new[] { "core.fsmonitor=false", "core.hooksPath=/dev/null", "credential.helper=", "core.quotePath=false" })
		{
			start.ArgumentList.Add("-c");
			start.ArgumentList.Add(setting);
		}
		foreach (var argument in arguments) start.ArgumentList.Add(argument);
		start.Environment["GIT_TERMINAL_PROMPT"] = "0";
		start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
		start.Environment["GIT_PAGER"] = "cat";
		start.Environment["LC_ALL"] = "C";
		if (environment is not null)
			foreach (var (key, value) in environment) start.Environment[key] = value;

		using var process = Process.Start(start) ?? throw new GitException("git could not be started.");
		using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timer.CancelAfter(timeout);
		try
		{
			var output = new MemoryStream();
			var reading = process.StandardOutput.BaseStream.CopyToAsync(output, timer.Token);
			var error = process.StandardError.ReadToEndAsync(timer.Token);
			if (input is not null) await process.StandardInput.BaseStream.WriteAsync(input, timer.Token);
			process.StandardInput.Close();
			await reading;
			var errorText = await error;
			await process.WaitForExitAsync(timer.Token);
			return new Result(process.ExitCode, output.ToArray(), errorText);
		}
		catch (OperationCanceledException)
		{
			try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
			if (cancellationToken.IsCancellationRequested) throw;
			throw new GitException($"git timed out after {timeout.TotalSeconds:0} s.");
		}
	}
}

public sealed class GitException(string message) : Exception(message);
