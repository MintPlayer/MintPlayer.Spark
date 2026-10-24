// The task behind MintPlayer.Versioning.MSBuild (docs/coverage_package_versioning_PRD.md D9). MSBuild compiles this
// file itself through RoslynCodeTaskFactory, so the package ships source and no task assembly: nothing is locked
// between builds, and a repository can import the targets straight from a checkout (Spark does, M14).
// ⚠️ C# 7.3 and netstandard2.0 only: RoslynCodeTaskFactory compiles against netstandard with its default language
// version. The version grammar and ordering mirror CodeCoverage.Versioning's PackageVersion; a parity test holds them
// together (CodeCoverage.Versioning.Tests, MSBuild/TagVersionTests), which compiles this file with nullable on.
#if NETCOREAPP
#nullable disable
#endif
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace MintPlayer.Versioning.MSBuild
{
	/// <summary>
	/// Sets a package's version from the highest <c>&lt;PackageId&gt;/v&lt;version&gt;</c> tag reachable from HEAD: the
	/// version the versioning engine last released (or seeded) for it, exactly, with no height suffix. A package that has
	/// no tag gets <see cref="TagVersion.Placeholder"/> and a warning.
	/// </summary>
	// Qualified: a project compiling this file with implicit usings also has System.Threading.Tasks.Task in scope.
	public class ResolveTagVersion : Microsoft.Build.Utilities.Task
	{
		/// <summary>The tag prefix, normally <c>$(PackageId)/v</c>.</summary>
		[Required]
		public string TagPrefix { get; set; }

		/// <summary>Where git runs: the project's directory, so a project inside a submodule reads that repository.</summary>
		[Required]
		public string WorkingDirectory { get; set; }

		[Output]
		public string Version { get; set; }

		public override bool Execute()
		{
			Version = TagVersion.Placeholder;
			if (!TagVersion.IsValidPrefix(TagPrefix))
			{
				Log.LogWarning(null, "MPV002", null, null, 0, 0, 0, 0,
					"'{0}' is not a tag prefix of a NuGet package id; the version stays {1}.", TagPrefix, TagVersion.Placeholder);
				return true;
			}

			string output, error;
			int exitCode;
			try
			{
				exitCode = Git(WorkingDirectory, "tag --merged HEAD --list \"" + TagPrefix + "*\"", out output, out error);
			}
			catch (Exception e)
			{
				Log.LogWarning(null, "MPV003", null, null, 0, 0, 0, 0,
					"git could not be run ({0}); the version stays {1}.", e.Message, TagVersion.Placeholder);
				return true;
			}
			if (exitCode != 0)
			{
				Log.LogWarning(null, "MPV003", null, null, 0, 0, 0, 0,
					"git tag failed ({0}); the version stays {1}.", error.Trim(), TagVersion.Placeholder);
				return true;
			}

			var version = TagVersion.Highest(output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries), TagPrefix);
			if (version == null)
			{
				Log.LogWarning(null, "MPV001", null, null, 0, 0, 0, 0,
					"No tag '{0}<version>' is reachable from HEAD, so the version is {1} and the project is not packed. A shallow clone has no tags: check out with fetch-depth: 0.",
					TagPrefix, TagVersion.Placeholder);
				return true;
			}
			Version = version;
			Log.LogMessage(MessageImportance.Normal, "MintPlayer.Versioning: {0}{1}", TagPrefix, version);
			return true;
		}

		private static int Git(string directory, string arguments, out string output, out string error)
		{
			var info = new ProcessStartInfo("git", arguments)
			{
				WorkingDirectory = directory,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
			};
			using (var process = Process.Start(info))
			{
				// Read stderr asynchronously so neither pipe can fill up and deadlock the other.
				var stderr = process.StandardError.ReadToEndAsync();
				output = process.StandardOutput.ReadToEnd();
				process.WaitForExit();
				error = stderr.Result;
				return process.ExitCode;
			}
		}
	}

	/// <summary>The version grammar and ordering of the engine's tags (PRD D5), without the engine.</summary>
	public static class TagVersion
	{
		public const string Placeholder = "0.0.0-PLACEHOLDER";

		private static readonly Regex Grammar = new Regex(
			@"^(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})(?:-(alpha|beta|next|preview|rc)\.(0|[1-9][0-9]{0,8}))?\z",
			RegexOptions.CultureInvariant);

		// A NuGet package id followed by "/v". The id cannot hold a git glob character, so the prefix is safe to pass
		// to `git tag --list` as a pattern.
		private static readonly Regex Prefix = new Regex(@"^[A-Za-z0-9_][A-Za-z0-9_.-]{0,99}/v\z", RegexOptions.CultureInvariant);

		// In order of maturity, which is also semver's alphabetical order.
		private static readonly string[] Labels = { "alpha", "beta", "next", "preview", "rc" };

		public static bool IsValidPrefix(string prefix) => prefix != null && Prefix.IsMatch(prefix);

		/// <summary>
		/// The highest version among the tag names that start with <paramref name="prefix"/>. Names whose version is not
		/// one of ours (a human's <c>v1.2</c>, build metadata, another label) are ignored, as the engine ignores them.
		/// </summary>
		public static string Highest(IEnumerable<string> tags, string prefix)
		{
			string best = null;
			long[] bestKey = null;
			foreach (var raw in tags)
			{
				var tag = raw.Trim();
				if (!tag.StartsWith(prefix, StringComparison.Ordinal)) continue;
				var version = tag.Substring(prefix.Length);
				var key = Key(version);
				if (key == null) continue;
				if (bestKey == null || Compare(key, bestKey) > 0)
				{
					best = version;
					bestKey = key;
				}
			}
			return best;
		}

		/// <summary>Orders two versions of our grammar; a version outside it sorts below every valid one.</summary>
		public static int Compare(string left, string right)
		{
			var l = Key(left);
			var r = Key(right);
			if (l == null || r == null) return (l != null).CompareTo(r != null);
			return Compare(l, r);
		}

		// major, minor, patch, then the label's rank (a stable version ranks above every label) and its number.
		private static long[] Key(string version)
		{
			if (version == null || version.Length > 64) return null;
			var match = Grammar.Match(version);
			if (!match.Success) return null;
			var key = new long[5];
			for (var i = 0; i < 3; i++) key[i] = long.Parse(match.Groups[i + 1].Value, NumberStyles.None, CultureInfo.InvariantCulture);
			key[3] = match.Groups[4].Success ? Array.IndexOf(Labels, match.Groups[4].Value) : Labels.Length;
			key[4] = match.Groups[5].Success ? long.Parse(match.Groups[5].Value, NumberStyles.None, CultureInfo.InvariantCulture) : 0;
			return key;
		}

		private static int Compare(long[] left, long[] right)
		{
			for (var i = 0; i < left.Length; i++)
			{
				var c = left[i].CompareTo(right[i]);
				if (c != 0) return c;
			}
			return 0;
		}
	}
}
