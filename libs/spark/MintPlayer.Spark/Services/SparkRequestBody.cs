using System.Text.Json;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Reads a typed request body without letting a malformed one become a 500.
/// </summary>
/// <remarks>
/// <para>
/// Every Spark endpoint now takes its parameters from a JSON body, including the reads. That makes
/// "the caller sent rubbish" an ordinary case rather than an exotic one, and an unhandled
/// <see cref="JsonException"/> out of <c>ReadFromJsonAsync</c> is a 500 with a stack trace — which is
/// both noise and, on the endpoints that resolve a type, a way to tell a parse failure apart from a
/// refusal.
/// </para>
/// <para>
/// A <see langword="null"/> here means "nothing usable arrived", and every caller answers it with the
/// same refusal it gives an unknown type or id.
/// </para>
/// </remarks>
internal static class SparkRequestBody
{
    public static async Task<TRequest?> ReadAsync<TRequest>(HttpContext httpContext)
        where TRequest : class
    {
        try
        {
            return await httpContext.Request.ReadFromJsonAsync<TRequest>();
        }
        catch (JsonException)
        {
            return null;
        }
        catch (BadHttpRequestException)
        {
            // Wrong or missing Content-Type, or a body over the configured size limit.
            return null;
        }
    }
}
