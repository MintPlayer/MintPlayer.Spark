# MintPlayer.Spark.SafeHttp

SSRF-safe named `HttpClient`s for fetching URLs that users supply (a site to audit, a JWKS URI, a webhook target).

```csharp
services.AddSafeHttpClient("MyApp.Fetcher", configuration.GetSection("MyApp:Fetcher"), o => o.MaxRedirects = 2);
var client = httpClientFactory.CreateClient("MyApp.Fetcher");
```

- Resolves the host, keeps only global unicast addresses (IPv4-mapped, NAT64 and 6to4 unwrapped) and connects to the
  address it checked, so DNS rebinding cannot swap it.
- Follows redirects itself and checks every hop again; optional `RedirectsWithinOriginOnly`; `FollowRedirects = false`
  returns a redirect as it is.
- No proxy, no cookies; caps on wire bytes, decompressed bytes and time.
- `SafeHttpTrace` records the hops and the wire `Content-Encoding` of one request; `ISafeHttpSockets` opens a raw
  connection under a named client's rules (for a TLS probe).

A refusal is a `SafeHttpBlockedException` whose message names no internal detail.

In a Spark application `spark.AddSafeHttp()` (package `MintPlayer.Spark`) registers the client `Spark.SafeHttp`
bound to `Spark:SafeHttp`. This package carries no RavenDB client and no Spark runtime, so a host that must not reach
a database (SeoTester's fetcher container) can use it on its own.
