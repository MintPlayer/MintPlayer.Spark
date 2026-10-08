using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace MintPlayer.Spark.Authorization.Extensions;

/// <summary>
/// The X preset's backchannel handler (#490 D6, Q3): moves the confidential-client credentials of the
/// token request from the form body into an HTTP Basic <c>Authorization</c> header, which is how X
/// authenticates a confidential client.
/// </summary>
/// <remarks>
/// ASP.NET's OAuth handler always posts <c>client_id</c> and <c>client_secret</c> in the body
/// (client_secret_post). Only a POST whose form carries a <c>client_secret</c> — the token request —
/// is rewritten; the user lookup and every other request pass through untouched. The header value is
/// <c>base64(urlencode(client_id) ":" urlencode(client_secret))</c> (RFC 6749 §2.3.1); <c>client_id</c>
/// stays in the body, as X's own examples send it.
/// </remarks>
internal sealed class SparkXClientAuthenticationHandler(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
{
    internal const string ClientIdParameter = "client_id";
    internal const string ClientSecretParameter = "client_secret";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Post && request.Content is FormUrlEncodedContent form)
        {
            var fields = QueryHelpers.ParseQuery(await form.ReadAsStringAsync(cancellationToken));
            if (fields.TryGetValue(ClientSecretParameter, out var secret))
            {
                fields.TryGetValue(ClientIdParameter, out var clientId);

                var credentials = Uri.EscapeDataString(clientId.ToString()) + ":" + Uri.EscapeDataString(secret.ToString());
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)));

                var body = fields
                    .Where(field => !string.Equals(field.Key, ClientSecretParameter, StringComparison.Ordinal))
                    .SelectMany(field => field.Value.Select(value => new KeyValuePair<string, string>(field.Key, value ?? string.Empty)))
                    .ToList();
                request.Content = new FormUrlEncodedContent(body);
                form.Dispose();
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
