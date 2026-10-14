using System.Text;
using CodeCoverage.GitMirror.Tests._Infrastructure;

namespace CodeCoverage.GitMirror.Tests.Mirror;

/// <summary>
/// S1 and S7 as automated tests (PRD §5.3, §5.4; D5, D26). The token is never printed: every assertion is a boolean whose
/// message does not include it.
/// </summary>
public class MirrorSecurityTests : MirrorTestBase
{
    private const string User = "x-access-token";

    private static string NewToken() => "ghs_" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")[..8];

    private static string Basic(string token) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{token}"));

    [Fact]
    public async Task A_token_clone_and_fetch_succeed_and_the_token_is_nowhere_at_rest_in_arguments_or_logs()
    {
        var shas = CreateOrigin("private");
        var token = NewToken();
        Server.RequireCredential = (User, token);

        var cloned = await Sidecar.FetchAsync("github", "3001", Server.UrlOf("private"), credential: new { username = User, password = token });
        Push("private", "main", "e", 5, shas["c"]);
        var fetched = await Sidecar.FetchAsync("github", "3001", Server.UrlOf("private"), credential: new { username = User, password = token });

        cloned.Outcome.Should().Be("cloned");
        fetched.Outcome.Should().Be("fetched");
        fetched.CommitCount.Should().Be(5);
        Server.AuthorizedRequests.Should().BeGreaterThan(0, "the server checked the credential");

        var secrets = new[] { token, Basic(token) };
        AnyFileContains(Sidecar.MirrorOf("github", "3001"), secrets).Should().BeFalse("the token must not be at rest in the mirror (config, FETCH_HEAD, logs, …)");
        Sidecar.Started.Any(p => p.ArgumentList.Any(a => secrets.Any(a.Contains))).Should().BeFalse("the token must not be in any git argument");
        Sidecar.Logs.Lines.Any(l => secrets.Any(l.Contains)).Should().BeFalse("the token must not be in any log line");

        // Control: the token did travel, in the environment only (so the scans above look at the right bytes).
        Sidecar.Started.Count(p => p.Environment.TryGetValue("GIT_CONFIG_VALUE_0", out var v) && v!.Contains(Basic(token))).Should().BeGreaterThan(1);
        Sidecar.Started.Should().OnlyContain(p => !p.Environment.ContainsKey("GIT_CONFIG_VALUE_0") || p.Environment["GIT_CONFIG_KEY_0"] == "http.extraHeader");
    }

    [Fact]
    public async Task Control_without_the_token_the_server_refuses_and_the_outcome_is_auth()
    {
        CreateOrigin("private");
        Server.RequireCredential = (User, NewToken());

        var response = await Sidecar.FetchAsync("github", "3002", Server.UrlOf("private"));
        var wrong = await Sidecar.FetchAsync("github", "3003", Server.UrlOf("private"), credential: new { username = User, password = NewToken() });

        response.Outcome.Should().Be("failed");
        response.Reason.Should().Be("auth");
        wrong.Outcome.Should().Be("failed");
        wrong.Reason.Should().Be("auth");
        Server.RejectedRequests.Should().BeGreaterThan(0);
        Entries(Sidecar.Root).Where(e => e.Contains("3002") || e.Contains("3003")).Should().BeEmpty();
    }

    [Fact]
    public void Stderr_is_scrubbed_of_the_credential_before_it_is_returned_or_logged()
    {
        var token = NewToken();
        var credential = new CodeCoverage.Git.GitCredential(User, token);
        var text = $"fatal: header Authorization: Basic {Basic(token)} and {token}";

        var scrubbed = Sidecar.Runner.Scrub(text, credential);

        (scrubbed.Contains(token) || scrubbed.Contains(Basic(token))).Should().BeFalse();
        scrubbed.Should().Contain("***");
        credential.ToString().Contains(token).Should().BeFalse("a credential never prints its token");
    }

    [Fact]
    public async Task No_template_hooks_are_copied_and_a_hook_planted_in_the_mirror_never_fires()
    {
        var shas = CreateOrigin("repo");
        await Sidecar.FetchAsync("github", "3004", Server.UrlOf("repo"));
        var mirror = Sidecar.MirrorOf("github", "3004");

        // --template= (D5): the clone copied no hooks, not even samples. Control below: a default clone does.
        Entries(Path.Combine(mirror, "hooks")).Should().BeEmpty();
        using (var control = new TempDir("ctl"))
        {
            FixtureGit.Run(null, "clone", "--quiet", "--bare", Server.PathOf("repo"), control.Sub("plain.git"));
            Entries(control.Sub(Path.Combine("plain.git", "hooks"))).Should().NotBeEmpty("git's default template carries hook samples");
        }

        // Plant a reference-transaction hook (it runs on every ref update a fetch makes) where a template would put it.
        using var markers = new TempDir("hooks");
        var marker = markers.Sub("fired").Replace('\\', '/');
        var hook = Path.Combine(mirror, "hooks", "reference-transaction");
        Directory.CreateDirectory(Path.GetDirectoryName(hook)!);
        File.WriteAllText(hook, $"#!/bin/sh\necho fired >> \"{marker}\"\nexit 0\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Push("repo", "main", "e", 5, shas["c"]);
        var fetched = await Sidecar.FetchAsync("github", "3004", Server.UrlOf("repo"));
        fetched.Outcome.Should().Be("fetched");
        File.Exists(marker).Should().BeFalse("core.hooksPath points at the empty read-only directory");

        // Control: the same fetch with the mirror's hooks directory as hooksPath does run the hook.
        Push("repo", "main", "f", 6, FixtureGit.Run(Server.PathOf("repo"), "rev-parse", "main").Trim());
        FixtureGit.Run(mirror, "-c", $"core.hooksPath={Path.Combine(mirror, "hooks")}", "fetch", "--quiet", "origin");
        File.Exists(marker).Should().BeTrue("the planted hook is executable and fires on a fetch, so the assertion above can fail");
    }
}
