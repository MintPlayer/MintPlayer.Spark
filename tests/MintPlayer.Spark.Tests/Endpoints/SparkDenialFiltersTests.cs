using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Endpoints;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Tests.Endpoints;

/// <summary>
/// #521 X20 item 9: the endpoint filters that replaced the per-endpoint
/// <c>catch (SparkAccessDeniedException) { return refusal; }</c> blocks. Pinned against a running
/// pipeline, because the property they rest on — a route handler's exception reaches an endpoint
/// filter, and the filter's return value becomes the response — is framework behaviour.
/// <para>
/// The answers must be byte-for-byte the ones the handlers gave themselves: <see cref="SparkDenial"/>'s
/// 401 for an anonymous caller (when authenticating would help) and 404 otherwise, bare for
/// <c>/spark/lookupref/*</c> and inside the <c>{ result, operations }</c> envelope everywhere else, with
/// the operations the handler queued before the denial still in it.
/// </para>
/// </summary>
public class SparkDenialFiltersTests
{
    private const string Scheme = "Probe";

    private enum Caller { Anonymous, SignedIn }

    /// <summary>
    /// A host with one route per behaviour, each wrapped by <paramref name="filter"/>. No Spark, no
    /// RavenDB: the subject is the filter.
    /// </summary>
    private static async Task<IHost> StartHostAsync(Func<RouteHandlerBuilder, RouteHandlerBuilder> filter, Caller caller)
    {
        var registry = new SparkModuleRegistry();
        // A credential scheme means signing in could help, so an anonymous refusal is 401 (SparkDenial).
        registry.AddCredentialScheme(Scheme);

        return await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton(registry);
                    services.AddScoped<IClientAccessor>(_ => new ClientAccessor());
                })
                .Configure(app =>
                {
                    if (caller == Caller.SignedIn)
                    {
                        app.Use((context, next) =>
                        {
                            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "probe-user")], Scheme));
                            return next(context);
                        });
                    }

                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        filter(endpoints.MapPost("/denied", IResult (IClientAccessor client) =>
                        {
                            client.Notify("queued before the denial");
                            throw new SparkAccessDeniedException("Edit/Car");
                        }));
                        filter(endpoints.MapPost("/row-denied", IResult () => throw new SparkRowLevelAccessDeniedException("Edit/Car")));
                        filter(endpoints.MapPost("/fails", IResult () => throw new InvalidOperationException("not a denial")));
                        filter(endpoints.MapPost("/ok", () => Results.Json(new { ok = true })));
                    });
                }))
            .StartAsync();
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(IHost host, string path)
    {
        using var client = host.GetTestClient();
        using var response = await client.PostAsync(path, content: null);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
    }

    // ── RefuseDeniedAsJson (lookup references) ───────────────────────────────────────────────

    [Fact]
    public async Task Json_filter_answers_an_anonymous_denial_with_the_uniform_401()
    {
        using var host = await StartHostAsync(b => b.RefuseDeniedAsJson(), Caller.Anonymous);

        var (status, body) = await PostAsync(host, "/denied");

        status.Should().Be(HttpStatusCode.Unauthorized);
        body.GetProperty("error").GetString().Should().Be("Authentication required");
    }

    [Fact]
    public async Task Json_filter_answers_a_signed_in_denial_with_the_uniform_404()
    {
        using var host = await StartHostAsync(b => b.RefuseDeniedAsJson(), Caller.SignedIn);

        var (status, body) = await PostAsync(host, "/denied");

        status.Should().Be(HttpStatusCode.NotFound);
        body.GetProperty("error").GetString().Should().Be(SparkDenial.NotFoundMessage);
        body.TryGetProperty("operations", out _).Should().BeFalse("the lookup-reference refusal is bare, never enveloped");
    }

    // ── RefuseDeniedInEnvelope (persistent objects, actions, add-ons) ─────────────────────────

    [Fact]
    public async Task Envelope_filter_answers_an_anonymous_denial_with_the_uniform_401_in_the_envelope()
    {
        using var host = await StartHostAsync(b => b.RefuseDeniedInEnvelope(), Caller.Anonymous);

        var (status, body) = await PostAsync(host, "/denied");

        status.Should().Be(HttpStatusCode.Unauthorized);
        body.GetProperty("result").GetProperty("error").GetString().Should().Be("Authentication required");
    }

    [Fact]
    public async Task Envelope_filter_answers_a_signed_in_denial_with_the_uniform_404_and_keeps_queued_operations()
    {
        using var host = await StartHostAsync(b => b.RefuseDeniedInEnvelope(), Caller.SignedIn);

        var (status, body) = await PostAsync(host, "/denied");

        status.Should().Be(HttpStatusCode.NotFound);
        body.GetProperty("result").GetProperty("error").GetString().Should().Be(SparkDenial.NotFoundMessage);
        body.GetProperty("operations").GetArrayLength().Should().Be(1,
            "the filter reads the request's own scoped IClientAccessor, as the handler's catch did");
    }

    [Fact]
    public async Task Envelope_filter_answers_a_row_level_denial_exactly_like_a_type_level_one()
    {
        // R2-H2 / M-3: the endpoints used to catch SparkRowLevelAccessDeniedException separately, with
        // the same refusal; the filter catches it as the SparkAccessDeniedException it derives from.
        using var host = await StartHostAsync(b => b.RefuseDeniedInEnvelope(), Caller.SignedIn);

        var (status, body) = await PostAsync(host, "/row-denied");

        status.Should().Be(HttpStatusCode.NotFound);
        body.GetProperty("result").GetProperty("error").GetString().Should().Be(SparkDenial.NotFoundMessage);
    }

    // ── what the filters leave alone ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_successful_handler_passes_through_unchanged(bool envelope)
    {
        using var host = await StartHostAsync(b => envelope ? b.RefuseDeniedInEnvelope() : b.RefuseDeniedAsJson(), Caller.SignedIn);

        var (status, body) = await PostAsync(host, "/ok");

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("ok").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Any_other_exception_is_not_turned_into_a_refusal(bool envelope)
    {
        using var host = await StartHostAsync(b => envelope ? b.RefuseDeniedInEnvelope() : b.RefuseDeniedAsJson(), Caller.SignedIn);
        using var client = host.GetTestClient();

        var act = () => client.PostAsync("/fails", content: null);

        // TestServer rethrows an unhandled server exception into the client call: the filter let it go.
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("not a denial");
    }
}
