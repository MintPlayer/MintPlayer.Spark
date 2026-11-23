using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodeCoverage.Versioning.Changelog;

/// <summary>
/// <see cref="IPullRequestFacts"/> from GitHub's GraphQL API (#693): one query per <see cref="BatchSize"/> pull requests,
/// each an alias of its own number. A batch that fails is a warning and leaves its pull requests out, so the notes fall
/// back to the commit subjects rather than failing the release.
/// </summary>
public sealed partial class GitHubPullRequestFacts(HttpClient http, string graphQlUrl, string repository, string token, Action<string> warn) : IPullRequestFacts
{
	public const int BatchSize = 50;

	/// <summary>The GraphQL endpoint of a REST API root: <c>https://api.github.com/graphql</c>, or GHES's <c>/api/graphql</c>.</summary>
	public static string GraphQlUrl(string apiUrl)
	{
		var root = apiUrl.TrimEnd('/');
		return root.EndsWith("/api/v3", StringComparison.OrdinalIgnoreCase) ? root[..^"/v3".Length] + "/graphql" : root + "/graphql";
	}

	public async Task<IReadOnlyDictionary<int, PullRequestFacts>> GetAsync(IReadOnlyCollection<int> numbers, CancellationToken cancellationToken = default)
	{
		var facts = new Dictionary<int, PullRequestFacts>();
		if (repository.Split('/') is not [var owner, var name] || owner.Length == 0 || name.Length == 0) return facts;
		foreach (var batch in numbers.Where(n => n > 0).Distinct().Chunk(BatchSize))
		{
			try
			{
				foreach (var pull in await QueryAsync(owner, name, batch, cancellationToken)) facts[pull.Number] = pull;
			}
			catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException)
			{
				warn($"Reading pull requests {string.Join(", ", batch.Select(n => $"#{n}"))} failed, so their notes come from the commit subjects: {e.Message}");
			}
		}
		return facts;
	}

	private async Task<IReadOnlyList<PullRequestFacts>> QueryAsync(string owner, string name, int[] numbers, CancellationToken cancellationToken)
	{
		// The numbers are ints, so the aliases and arguments cannot carry anything else; owner and name are variables.
		var fields = string.Join(' ', numbers.Select(n =>
			$"pr{n}: pullRequest(number: {n}) {{ number title body labels(first: 20) {{ nodes {{ name }} }} closingIssuesReferences(first: 10) {{ nodes {{ labels(first: 20) {{ nodes {{ name }} }} }} }} }}"));
		var query = $"query($owner: String!, $name: String!) {{ repository(owner: $owner, name: $name) {{ {fields} }} }}";
		using var request = new HttpRequestMessage(HttpMethod.Post, graphQlUrl)
		{
			Content = new StringContent(JsonSerializer.Serialize(new { query, variables = new { owner, name } }), Encoding.UTF8, "application/json"),
		};
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
		request.Headers.UserAgent.ParseAdd("codecoverage-versioning");
		using var response = await http.SendAsync(request, cancellationToken);
		response.EnsureSuccessStatusCode();
		using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
		return Read(document.RootElement, repository);
	}

	/// <summary>
	/// The pull requests of one answer. A number that is an issue, not a pull request, comes back null with an error
	/// beside it: it is simply absent.
	/// </summary>
	internal static IReadOnlyList<PullRequestFacts> Read(JsonElement root, string repository)
	{
		var found = new List<PullRequestFacts>();
		if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
			|| !data.TryGetProperty("repository", out var repo) || repo.ValueKind != JsonValueKind.Object)
			return found;
		foreach (var alias in repo.EnumerateObject())
		{
			var pull = alias.Value;
			if (pull.ValueKind != JsonValueKind.Object || !pull.TryGetProperty("number", out var number)
				|| number.ValueKind != JsonValueKind.Number || !number.TryGetInt32(out var n)) continue;
			var body = String(pull, "body");
			var bug = pull.TryGetProperty("closingIssuesReferences", out var issues) && Nodes(issues)
				.Any(issue => issue.TryGetProperty("labels", out var labels) && Names(labels).Any(l => string.Equals(l, "bug", StringComparison.OrdinalIgnoreCase)));
			found.Add(new PullRequestFacts(n, String(pull, "title") ?? "", body,
				pull.TryGetProperty("labels", out var own) ? [.. Names(own)] : [], bug, BackportOf(body, repository)));
		}
		return found;
	}

	/// <summary>The original a backport PR's body names: <c>&lt;!-- backport-of: owner/repo#609 --&gt;</c>, of this repository only.</summary>
	internal static int? BackportOf(string? body, string repository)
	{
		if (body is null || BackportMarker().Match(body) is not { Success: true } m) return null;
		if (m.Groups["repo"].Success && !string.Equals(m.Groups["repo"].Value, repository, StringComparison.OrdinalIgnoreCase)) return null;
		return int.TryParse(m.Groups["n"].Value, out var n) && n > 0 ? n : null;
	}

	private static string? String(JsonElement element, string property)
		=> element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

	private static IEnumerable<JsonElement> Nodes(JsonElement connection)
		=> connection.ValueKind == JsonValueKind.Object && connection.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array
			? nodes.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.Object)
			: [];

	private static IEnumerable<string> Names(JsonElement labels) => Nodes(labels).Select(l => String(l, "name")).OfType<string>();

	[GeneratedRegex(@"<!--\s*backport-of:\s*(?<repo>[A-Za-z0-9-]+/[A-Za-z0-9._-]+)?#(?<n>\d{1,9})\s*-->", RegexOptions.IgnoreCase)]
	private static partial Regex BackportMarker();
}
