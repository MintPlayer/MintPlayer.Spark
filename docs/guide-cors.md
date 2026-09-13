# CORS

Which of your endpoints a page on **another origin** may read, and how to change it.

## The one thing to understand first

**CORS is not an access control. It is a relaxation of one the browser already applies.**

A server does not "reject" a cross-origin request via CORS — it omits the `Access-Control-Allow-Origin`
header, and the *browser* then refuses to let the calling page read the response. Absence of a policy
**is** the refusal. A policy allowing zero origins emits no header either, so it behaves identically to
having no policy at all: there is no stronger "deny" to configure.

Two consequences worth holding on to:

- **It only constrains browsers.** Anything a cross-origin page can read, a server, a script or `curl`
  could already fetch directly — CORS never entered into it. Adding the header does not make data
  public that was private; it makes data already public to every other client readable by a browser
  page too.
- **It says nothing about *who* the caller is.** That is authentication and `security.json`. A
  cross-origin read gets the **anonymous** view, because a browser does not attach cookies to a
  cross-origin request unless the response also grants credentials — which Spark's policy deliberately
  cannot do (see [Credentials](#credentials-and-why-the-wildcard)).

---

## Three tiers

| Whose endpoint | Cross-origin by default? | How to change it |
|---|---|---|
| **Spark's own** — anything under `/spark` | **Yes**, `Access-Control-Allow-Origin: *` | opt out per endpoint |
| **A library's**, outside that prefix | No | opt in per endpoint |
| **Your application's** | No | opt in per endpoint, or set your own default policy |

### Spark's own endpoints

Every framework endpoint answers cross-origin without anyone asking. Spark's API is a public,
credential-free read: the anonymous view of `/spark/types`, `/spark/queries` or a permission check is
already fetchable by any HTTP client, so withholding the header only stopped browsers doing what
nothing else was stopped from doing.

To take it off one endpoint:

```csharp
endpoints.MapGet("/spark/internal-thing", Handler)
         .WithMetadata(new DisableCorsAttribute());   // Microsoft.AspNetCore.Cors
```

> ⚠️ **There is no `.DisableCors()` builder extension.** `RequireCors(...)` exists, which makes the
> symmetry very natural to assume, but opting out is the metadata form above. Assuming otherwise costs
> a compile error, which is the good case — the bad case is assuming it in a comment and misleading the
> next reader.

### A library's endpoints

A library that mounts endpoints outside `/spark` gets nothing by default and asks per endpoint:

```csharp
connectGroup.MapPost("/token", Token.Handle).RequireCors(SparkExtensions.SparkCorsPolicy);
```

`SparkCorsPolicy` is public so you can reuse it rather than registering a near-identical one. Register
your own named policy instead when you need different rules.

> ⚠️ **Ask only for the endpoints that a browser actually calls with `fetch`.** An endpoint reached by
> top-level navigation — a login page, a consent screen, an OAuth `authorize` redirect — is not a CORS
> request at all, so opting it in grants something no caller can use while widening what you have to
> reason about. Spark's identity provider opts in five endpoints and leaves the rest of `/connect`
> alone for exactly this reason.

### Your application's endpoints

Same as a library: nothing by default, `RequireCors` to opt in. You may also register your own
**default** policy, which applies to your endpoints without naming it:

```csharp
services.AddCors(cors => cors.AddDefaultPolicy(p => p.WithOrigins("https://app.example.com")));
```

⚠️ That default does **not** reach `/spark/*`. Spark applies its own named policy to its own prefix, so
the framework's cross-origin surface stays predictable and does not change because of a convenience
default you set for your controllers.

---

## Credentials, and why the wildcard

Spark's policy is `AllowAnyOrigin()`, which emits a literal `*` rather than echoing the caller's origin.
That is deliberate, and it is a safety property rather than a style choice:

**ASP.NET Core refuses to combine `AllowCredentials()` with any-origin.** So the wildcard makes the
dangerous configuration impossible to reach by accident — nobody can widen this into cross-origin
access to a *signed-in* user's data without first hitting that refusal and having to think about it.
Echoing the origin would look identical in a response and leave that door open.

The practical upshot: a cross-origin `fetch` to a Spark endpoint sends no cookies, so it is
unauthenticated and sees the anonymous view. `credentials: 'include'` does not change that — the
browser requires `Access-Control-Allow-Credentials: true` alongside a *specific* origin, and Spark
sends neither.

---

## ⚠️ When the default is wrong for you: private networks

The one deployment where this genuinely changes exposure is a Spark application **not reachable from
the internet** — an intranet app, or one behind a VPN.

Normally CORS adds nothing, because an attacker's own server could fetch the same anonymous data
directly. On a private network it cannot: it has no route. But a public page that one of your users
visits *does* — their browser is inside the network — and with the header present that page can read
the response.

If that matters for your deployment, opt the endpoints out. There is no global switch, deliberately:
a blanket "turn CORS off" would be indistinguishable from having no policy at all for every other
deployment, and the per-endpoint form makes the decision visible where it applies.

---

## The identity provider

`AddIdentityProvider` has its own switch, off by default:

```csharp
spark.AddIdentityProvider(options => options.EnableDynamicCors = true);
```

It exists for a **browser-based OIDC client on a different origin** — a SPA doing authorization-code +
PKCE against your provider, hosted elsewhere. Your own Angular frontend does not need it: Spark serves
it from the same host, so every call to `/connect/token` is same-origin.

When on, it opts in discovery, JWKS, `token`, `userinfo` and `revoke`. `introspect` is deliberately
left out — it is a resource-server call authenticated by client credentials, so a browser has no
business making it.

> ⚠️ Turning it on currently allows **any** origin, not the origins registered in an application's
> `AllowedCorsOrigins`. That property exists on the model and is read by nothing; narrowing to it is
> tracked in [leftovers](leftovers.md). Any-origin is defensible here — a public OIDC client has no
> secret, so `/token` is protected by PKCE rather than by `Origin` — but it is broader than the model
> implies.

---

## Related

- **[HTTP API Specification](Spark-API-Specification.md)** — every endpoint, and what each requires
- **[Authorization](guide-authorization.md)** — `security.json`, which is what actually decides who sees what
- **[Authentication Schemes](guide-authentication-schemes.md)** — how a caller becomes someone
