# Embed mode: selling on the organiser's own site

Embed mode (`docs/commerce_PRD.md` X23, GB8, §4.14) lets an organiser put a platform page, such as an event's ticket
list, on their own site with one script tag. Pretix's widget and Eventbrite's embedded checkout are the precedents.

The hard part is cookies. Inside an iframe on `organiser.example`, every cookie of `tickets.example` is a
**third-party cookie**: the visitor cookie (X1), the session and the XSRF token. Browsers block those more and more
(Safari and Firefox by default, Chrome when the user chooses). An embed therefore **never relies on them**. It only
reads public data, and as soon as something needs the visitor's identity (a hold, a sign-in, a waiting room) it
**hands over** to a top-level window on the platform.

---

## Server

```csharp
spark.AddEmbed(o =>
{
    o.AllowedOrigins.Add("https://organiser.example");
    o.AllowedOrigins.Add("https://*.festival.example");   // every subdomain, not festival.example itself
    // o.PathPrefixes = ["/embed"];                        // the SPA routes that may be framed (default)
});
```

Or set `Spark:Embed` (`Enabled`, `AllowedOrigins`, `PathPrefixes`, `DenyFramingElsewhere`, `HandoverLifetime`,
`PublicOrigin`) in configuration.

**What changes:**

| | |
|---|---|
| HTML under `/embed/…` | `Content-Security-Policy: frame-ancestors 'self' <allowed origins>`; `X-Frame-Options` removed, since it cannot list origins |
| every other HTML response | `frame-ancestors 'self'`, so the rest of the platform cannot be framed (clickjacking). Turn off with `DenyFramingElsewhere = false` only if the app sets its own policy |
| `GET /spark/embed/loader.js` | the organiser's script (below) |
| `GET /spark/embed/config` | the allowed origins, which the embed uses as its only `postMessage` targets |
| `POST /spark/embed/handover` | signs a hand-over: `{ path, payload?, returnUrl? }` → `{ url, expiresAt }` |
| `POST /spark/embed/handover/redeem` | `{ token }` → `{ path, payload, returnUrl }`, or 404 when forged or expired |

The policy is **appended**, never replacing the app's own CSP: several policies are all enforced. If the app already
sends `frame-ancestors 'none'`, embedding stays blocked.

**Startup check.** Each origin must be `https://host[:port]`, `https://*.host[:port]` or `http://localhost[:port]`.
Anything else (`*`, a path, a `;`) refuses the app, because it would end up inside a CSP header.

**The hand-over token** is Data Protection-protected and time-limited (`HandoverLifetime`, default 15 minutes). It
carries:

- the platform path, which must be a rooted path: no `//`, no `\`, no scheme;
- at most 2 KB of JSON payload (the selection);
- an optional `returnUrl`, accepted only on an allowed embedding origin. That makes "back to the organiser" safe to
  follow, with no open redirect.

The token grants no authority. Whatever acts on the payload validates it again, like any request.

---

## The organiser's page

```html
<script src="https://tickets.example/spark/embed/loader.js" async></script>
<spark-embed src="/embed/events/summer-fest" title="Tickets for Summer Fest">
  <a href="https://tickets.example/events/summer-fest">Buy tickets</a>   <!-- shown without JavaScript -->
</spark-embed>
```

The loader defines `<spark-embed>`, a plain web component with no build step. It replaces its children with an
iframe onto the platform's embed route and listens for two messages:

- `spark-embed:resize` `{ height }` sizes the iframe;
- `spark-embed:navigate` `{ url }` navigates the organiser's page to a **platform** URL. This is the hand-over
  fallback when a popup is blocked.

A message is accepted only from that iframe's window and the platform's origin, and a navigation only to the
platform's origin. An organiser who prefers a bare `<iframe>` gets the same embed without resizing; the hand-over then
uses the popup or the frame's own top navigation.

---

## The embed route (Angular): `@mintplayer/ng-spark/embed`

```ts
// /embed/events/:slug: a light layout, public reads only
export class EmbedEventComponent {
  private readonly embed = inject(SparkEmbedService);
  constructor() {
    const stop = this.embed.startResizeReporting();
    inject(DestroyRef).onDestroy(stop);
  }

  // From the click handler itself, so the popup opens inside the user's gesture.
  buy(selection: Selection) {
    void this.embed.handOver({ path: '/checkout', payload: selection, returnUrl: this.organiserPage });
  }

  // A waiting-room sale always opens top-level: a queue position must survive in a real tab (§4.14).
  join() {
    void this.embed.handOver({ path: `/queue/${this.saleId}` }, 'top');
  }
}
```

```html
<spark-embed-continue />   <!-- shows "Continue" as a link when nothing else was possible -->
```

`handOver` tries the routes in order and reports the one it used:

1. **`popup`**: a blank window opened synchronously in the click, with `opener` cut, pointed at the signed URL
   once the server answers.
2. **`parent`**: the popup was blocked, so the loader navigates the organiser's page. The message is posted only
   to an allowed origin (`location.ancestorOrigins`, else the referrer, checked against `/spark/embed/config`).
3. **`top`**: the frame navigates its top window itself (works when the iframe is not sandboxed against it).
4. **`link`**: nothing worked (a sandboxed iframe, a strict blocker). `<spark-embed-continue>` shows the URL as a
   link. A link click is a fresh gesture, and browsers always allow it.

Outside a frame, `handOver` simply navigates (`redirect`).

**On the top-level page**, read the state back with `await embed.redeem()`, which takes `?spark-handover=` from the
URL. The visitor is now first-party: the visitor cookie, sign-in and holds work as usual. Send them back with the
redeemed `returnUrl` when done.

---

## Notes

- **Calls from the embed.** Reads under `/spark` answer CORS `*` without credentials, so a script-tag widget
  on the organiser's origin can read public data too. The hand-over endpoints need no XSRF token: they change
  nothing server-side, and the XSRF cookie is third-party inside a frame.
- **Storage Access API.** Not used. Asking for storage access would show a browser prompt inside someone else's page,
  and the hand-over makes it unnecessary.
- **The commerce widget** (`<spark-commerce-widget>`, M17) is built on this mode. It lists offers in the embed and
  hands over for the hold, the sign-in and the waiting room.
