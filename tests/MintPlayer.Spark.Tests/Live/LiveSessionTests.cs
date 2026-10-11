using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Live;
using MintPlayer.Spark.Tests.Organizations;
using Xunit.Abstractions;

namespace MintPlayer.Spark.Tests.Live;

/// <summary>
/// Spike S8 L7 and L8 (PRD §12 "Spike S8"): a long socket against a real login cookie, and a save after a push.
/// </summary>
public class LiveSessionTests(ITestOutputHelper output) : OrganizationsTestHost
{
    private const string Password = "Correct-horse-1";
    private static readonly Uri Server = new("http://localhost/");

    private async Task<SparkUser> UserWithPasswordAsync(string name)
    {
        var user = await SeedUserAsync(name);
        using var scope = Factory.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<SparkUser>>();
        var stored = await users.FindByIdAsync(user.Id!);
        (await users.AddPasswordAsync(stored!, Password)).Succeeded.Should().BeTrue();
        return stored!;
    }

    /// <summary>Signs in over HTTP and returns the Identity cookie, as a browser would hold it.</summary>
    private async Task<string> SignInAsync(string userName)
    {
        using var client = Factory.CreateClient();
        var warmup = await client.GetAsync("/spark");
        string? antiforgery = null, xsrf = null;
        foreach (var raw in warmup.Headers.GetValues("Set-Cookie"))
        {
            var nameValue = raw.Split(';', 2)[0];
            if (nameValue.StartsWith(".AspNetCore.Antiforgery", StringComparison.Ordinal)) antiforgery = nameValue;
            else if (nameValue.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal)) xsrf = Uri.UnescapeDataString(nameValue["XSRF-TOKEN=".Length..]);
        }
        var request = new HttpRequestMessage(HttpMethod.Post, "/spark/auth/login?useCookies=true") { Content = JsonContent.Create(new { email = userName, password = Password }) };
        request.Headers.Add("Cookie", $"{antiforgery}; XSRF-TOKEN={Uri.EscapeDataString(xsrf!)}");
        request.Headers.Add("X-XSRF-TOKEN", xsrf);
        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return response.Headers.GetValues("Set-Cookie").Select(c => c.Split(';', 2)[0]).Single(c => c.StartsWith(".AspNetCore.Identity.Application=", StringComparison.Ordinal));
    }

    private Task<LiveTestClient> ConnectAsync(string cookie)
        => LiveTestClient.ConnectAsync(Factory.CreateWebSocketClient(), Server, request => request.Headers.Cookie = cookie);

    [Fact]
    public async Task L7_the_socket_closes_with_4401_at_the_login_tickets_expiry_and_a_reconnect_is_authenticated_afresh()
    {
        var lifetime = TimeSpan.FromSeconds(3);
        ExtraServices = services => services.PostConfigure<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme, o =>
        {
            o.ExpireTimeSpan = lifetime;
            o.SlidingExpiration = false;
        });
        var ann = await UserWithPasswordAsync("ann");
        var note = await SeedNoteAsync("v1", (await SeedOrganizationAsync("Acme", ann)).Id);
        var cookie = await SignInAsync(ann.UserName!);

        await using (var client = await ConnectAsync(cookie))
        {
            var clock = Stopwatch.StartNew();
            await client.SubscribePoAsync("n", NoteTypeId.ToString(), note.Id!);
            await client.NextAsync("po", "n");
            var status = await client.ClosedAsync(TimeSpan.FromSeconds(15));
            output.WriteLine($"L7 ticket lifetime {lifetime.TotalSeconds} s: socket closed with {(int?)status} after {clock.ElapsedMilliseconds} ms (connected right after sign-in)");
            status.Should().Be((WebSocketCloseStatus)LiveCodes.CloseReauthenticate);
            clock.Elapsed.Should().BeLessThan(lifetime + TimeSpan.FromSeconds(3));
        }

        // The same, now expired, cookie: the handshake is anonymous, and the note is gone for it.
        await using (var expired = await ConnectAsync(cookie))
        {
            await expired.SubscribePoAsync("n", NoteTypeId.ToString(), note.Id!);
            (await expired.NextAsync()).GetProperty("type").GetString().Should().Be("gone");
        }

        // A fresh sign-in: authenticated again on reconnect.
        await using (var renewed = await ConnectAsync(await SignInAsync(ann.UserName!)))
        {
            await renewed.SubscribePoAsync("n", NoteTypeId.ToString(), note.Id!);
            await renewed.NextAsync("po", "n");
        }
    }

    [Fact]
    public async Task L8_a_save_with_the_etag_from_before_a_push_is_still_refused_with_409()
    {
        // The push only brings the conflict forward; the server keeps the guarantee. An edit page that ignored
        // the banner and saves its old base must get the 409 and the merge, never overwrite.
        var ann = await SeedUserAsync("ann");
        var note = await SeedNoteAsync("v1", (await SeedOrganizationAsync("Acme", ann)).Id);
        await using var client = await LiveTestClient.ConnectAsync(Factory.CreateWebSocketClient(), Server,
            request => request.Headers[OgPrincipalStartupFilter.Header] = ann.Id);
        await client.SubscribePoAsync("n", NoteTypeId.ToString(), note.Id!);
        var mine = JsonNode.Parse((await client.NextAsync("po", "n")).GetProperty("po").GetRawText())!.AsObject();

        using (var session = Store.OpenAsyncSession())
        {
            (await session.LoadAsync<OgNote>(note.Id!))!.Title = "theirs";
            await session.SaveChangesAsync();
        }
        var pushed = await client.NextAsync("poPatch", "n");
        pushed.GetProperty("patch").GetProperty("set").GetProperty("etag").GetString().Should().NotBe(mine["etag"]!.GetValue<string>());

        var title = mine["attributes"]!.AsArray().First(a => a!["name"]!.GetValue<string>() == "Title")!.AsObject();
        title["value"] = "mine";
        title["isValueChanged"] = true;
        var (status, _) = await SendAsync("/spark/po/update", new JsonObject { ["objectTypeId"] = NoteTypeId.ToString(), ["id"] = note.Id, ["persistentObject"] = mine }, ann.Id);

        status.Should().Be(HttpStatusCode.Conflict);
        (await LoadAsync<OgNote>(note.Id!))!.Title.Should().Be("theirs");
    }
}
