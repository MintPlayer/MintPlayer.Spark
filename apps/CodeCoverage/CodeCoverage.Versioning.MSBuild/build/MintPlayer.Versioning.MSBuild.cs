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
	/// no tag gets a warning and <see cref="TagVersion.Untagged"/> in a build that cannot publish (see
	/// <see cref="TagVersion.PacksUntagged"/>), else <see cref="TagVersion.Placeholder"/>, which is never packed.
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

		/// <summary><c>$(GITHUB_ACTIONS)</c>: whether this build runs on GitHub Actions.</summary>
		public string GitHubActions { get; set; }

		/// <summary><c>$(GITHUB_EVENT_NAME)</c>: the event that started the workflow run.</summary>
		public string GitHubEventName { get; set; }

		/// <summary>
		/// <c>$(MintPlayerVersioningMajors)</c>: <c>id=major;…</c>, exported by the versioning action on a release line for
		/// each package anchored on the line's platform (#638 D2). A mapped package takes its highest tag of that major only.
		/// </summary>
		public string Majors { get; set; }

		/// <summary><c>$(GITHUB_REF_NAME)</c>: the branch (or <c>N/merge</c>, or a merge-queue branch) being built.</summary>
		public string GitHubRefName { get; set; }

		/// <summary><c>$(GITHUB_BASE_REF)</c>: a pull request's target branch.</summary>
		public string GitHubBaseRef { get; set; }

		[Output]
		public string Version { get; set; }

		/// <summary>
		/// Whether <c>Version</c> or <c>PackageVersion</c> is a global property (<c>-p:</c>, or passed down by a parent
		/// build): the caller owns the version, so nothing is resolved (#544 D1). A target's own assignment would
		/// override the global value, so the targets must know before they assign.
		/// </summary>
		[Output]
		public bool ExplicitVersion { get; set; }

		/// <summary>Whether <c>PackageVersion</c> alone is global, so <c>Version</c> follows it (#544 Q1).</summary>
		[Output]
		public bool ExplicitPackageVersionOnly { get; set; }

		public override bool Execute()
		{
			Version = TagVersion.Placeholder;
			var engine = BuildEngine as IBuildEngine6;
			var globals = engine != null ? engine.GetGlobalProperties() : null;
			if (globals != null)
			{
				// Property names are case-insensitive (`-p:version=` counts), whatever comparer the dictionary has.
				bool hasVersion = false, hasPackageVersion = false;
				foreach (var name in globals.Keys)
				{
					hasVersion |= string.Equals(name, "Version", StringComparison.OrdinalIgnoreCase);
					hasPackageVersion |= string.Equals(name, "PackageVersion", StringComparison.OrdinalIgnoreCase);
				}
				if (hasVersion || hasPackageVersion)
				{
					ExplicitVersion = true;
					ExplicitPackageVersionOnly = hasPackageVersion && !hasVersion;
					Log.LogMessage(MessageImportance.Normal, "MintPlayer.Versioning: the version is given explicitly; tags are not read.");
					return true;
				}
			}
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

			var tags = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
			var highest = TagVersion.Highest(tags, TagPrefix);
			var version = highest;
			var id = TagPrefix.Substring(0, TagPrefix.Length - 2);
			var major = TagVersion.MajorOf(Majors, id);
			if (major != null)
			{
				// #638 D2: on a release line, the action names the major of each package anchored on the line's platform.
				// A newer major's tag is reachable there (master's history), and packing it would publish nothing.
				version = TagVersion.Highest(tags, TagPrefix, major);
				if (version == null && highest != null)
				{
					if (TagVersion.PacksUntagged(GitHubActions, GitHubEventName)) Version = TagVersion.Untagged;
					Log.LogWarning(null, "MPV006", null, null, 0, 0, 0, 0,
						"No tag '{0}{1}.<minor>.<patch>' is reachable from HEAD on this release line (the highest is {2}), so the version is {3}. Tag the line's last release of {4} (the onboarding backfills it).",
						TagPrefix, major, highest, Version, id);
					return true;
				}
			}
			else if (string.IsNullOrWhiteSpace(Majors))
			{
				// Without the action (a pull request into a line, a local pack on it), a dotnet line's branch names the
				// major; a package with no tag of it (a netstandard package anchored freely) keeps its highest tag.
				var branch = TagVersion.BranchMajor(GitHubBaseRef, GitHubRefName);
				if (branch != null) version = TagVersion.Highest(tags, TagPrefix, branch) ?? highest;
			}
			if (version == null)
			{
				// A repository's onboarding pull request, or a package not released yet, has no tag. Its pull-request and
				// local builds still pack, at a version no engine tag can hold; a build that may publish does not.
				if (TagVersion.PacksUntagged(GitHubActions, GitHubEventName))
				{
					Version = TagVersion.Untagged;
					Log.LogWarning(null, "MPV001", null, null, 0, 0, 0, 0,
						"No tag '{0}<version>' is reachable from HEAD, so this pull-request or local build packs the project at {1}. A shallow clone has no tags: check out with fetch-depth: 0.",
						TagPrefix, TagVersion.Untagged);
				}
				else
				{
					Log.LogWarning(null, "MPV001", null, null, 0, 0, 0, 0,
						"No tag '{0}<version>' is reachable from HEAD, so the version is {1} and the project is not packed. A shallow clone has no tags: check out with fetch-depth: 0.",
						TagPrefix, TagVersion.Placeholder);
				}
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

		/// <summary>The version of an untagged package in a build that cannot publish. Its label is outside
		/// <see cref="Grammar"/>, so the engine never reads it as a release.</summary>
		public const string Untagged = "0.0.0-untagged";

		// The GitHub Actions events whose runs check a change before it lands. Their builds never publish.
		private static readonly string[] CheckEvents = { "pull_request", "pull_request_target", "merge_group" };

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
		/// Whether an untagged package packs at <see cref="Untagged"/>: in a local build, and in a GitHub Actions run of a
		/// pull request or merge queue. Any other CI run (a push, a schedule, a dispatch) is where packages are published,
		/// and there an untagged package keeps the placeholder and is not packed (the 14 placeholder packages of master
		/// run 40132026912 must not come back under another name).
		/// </summary>
		public static bool PacksUntagged(string gitHubActions, string gitHubEventName) =>
			!string.Equals(gitHubActions, "true", StringComparison.OrdinalIgnoreCase)
			|| Array.IndexOf(CheckEvents, gitHubEventName) >= 0;

		/// <summary>
		/// The highest version among the tag names that start with <paramref name="prefix"/>. Names whose version is not
		/// one of ours (a human's <c>v1.2</c>, build metadata, another label) are ignored, as the engine ignores them.
		/// </summary>
		public static string Highest(IEnumerable<string> tags, string prefix) => Highest(tags, prefix, null);

		/// <summary>As <see cref="Highest(IEnumerable{string}, string)"/>, among the versions of <paramref name="major"/> only
		/// when it is given.</summary>
		public static string Highest(IEnumerable<string> tags, string prefix, int? major)
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
				if (major != null && key[0] != major.Value) continue;
				if (bestKey == null || Compare(key, bestKey) > 0)
				{
					best = version;
					bestKey = key;
				}
			}
			return best;
		}

		private static readonly Regex Number = new Regex(@"^(0|[1-9][0-9]{0,8})\z", RegexOptions.CultureInvariant);

		private static readonly Regex DotnetLine = new Regex(@"^release/dotnet-(0|[1-9][0-9]{0,8})\.x\z", RegexOptions.CultureInvariant);

		private const string QueuePrefix = "gh-readonly-queue/";

		/// <summary>The major the action's <c>id=major;…</c> map gives <paramref name="id"/>, or null.</summary>
		public static int? MajorOf(string map, string id)
		{
			if (string.IsNullOrEmpty(map)) return null;
			foreach (var entry in map.Split(';'))
			{
				var equals = entry.IndexOf('=');
				if (equals < 0 || !string.Equals(entry.Substring(0, equals).Trim(), id, StringComparison.Ordinal)) continue;
				var value = entry.Substring(equals + 1).Trim();
				return Number.IsMatch(value) ? int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture) : (int?)null;
			}
			return null;
		}

		/// <summary>
		/// The major of the default-pattern dotnet line being built: a pull request's base, else a merge-queue branch's
		/// base (<c>gh-readonly-queue/&lt;base&gt;/pr-N-sha</c>), else the branch itself. Null anywhere else.
		/// </summary>
		public static int? BranchMajor(string baseRef, string refName)
		{
			var branch = !string.IsNullOrEmpty(baseRef) ? baseRef : refName;
			if (string.IsNullOrEmpty(branch)) return null;
			if (branch.StartsWith(QueuePrefix, StringComparison.Ordinal))
			{
				var end = branch.LastIndexOf("/pr-", StringComparison.Ordinal);
				if (end <= QueuePrefix.Length) return null;
				branch = branch.Substring(QueuePrefix.Length, end - QueuePrefix.Length);
			}
			var match = DotnetLine.Match(branch);
			return match.Success ? int.Parse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture) : (int?)null;
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
