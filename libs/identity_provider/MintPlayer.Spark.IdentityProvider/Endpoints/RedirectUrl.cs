using System.Text;

namespace MintPlayer.Spark.IdentityProvider.Endpoints;

/// <summary>
/// Builds the redirect URLs this provider sends a browser to.
/// <para>
/// Exists because every call site except one built them with an unconditional <c>?</c>, so a
/// client whose registered <c>redirect_uri</c> already carried a query string — a tenant id,
/// a locale — received <c>…?tenant=1?code=…</c>. Whether that parses is up to the client's
/// URL library, which is not a thing to leave to chance on the hop that delivers an
/// authorization code.
/// </para>
/// <para>
/// Empty values are omitted entirely rather than emitted as <c>name=</c>, which is what makes
/// "include <c>state</c> only if the client sent one" fall out instead of needing a guard at
/// each call site.
/// </para>
/// </summary>
internal static class RedirectUrl
{
    public static string With(string uri, params (string Name, string? Value)[] parameters)
    {
        var sb = new StringBuilder(uri);
        var hasQuery = uri.Contains('?');

        foreach (var (name, value) in parameters)
        {
            if (string.IsNullOrEmpty(value))
                continue;

            sb.Append(hasQuery ? '&' : '?')
              .Append(Uri.EscapeDataString(name))
              .Append('=')
              .Append(Uri.EscapeDataString(value));

            hasQuery = true;
        }

        return sb.ToString();
    }
}
