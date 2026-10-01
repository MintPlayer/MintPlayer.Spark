# Theming: light, dark and Auto

Every Spark app follows the operating system's colour scheme by default, and the user can pin Light or
Dark with the toggle in `spark-shell`'s topbar. The machinery is ng-bootstrap's (`@mintplayer/ng-bootstrap`
22.20+, `@mintplayer/web-components` 2.17+); Spark wires it into the shell and the apps. This guide is
what an app needs, how the modes behave, and how to change the colours.

Background and decisions: [issue_462_PRD.md](issue_462_PRD.md).

## 1. Setup per app

Three pieces. All five apps in `apps/` have them; copy them into a new one.

**The pre-boot script, copied into the build output.** In the app's `project.json`, under the build
target's `assets`:

```json
{
  "glob": "bs-theme-preboot.js",
  "input": "node_modules/@mintplayer/web-components/theming",
  "output": "/"
}
```

**`<head>` in this order** in `index.html`:

```html
<base href="/">
<meta name="color-scheme" content="light dark">
<meta name="bs-theme-default-mode" content="auto">
<meta name="theme-color" media="(prefers-color-scheme: light)" content="#f8f9fa">
<meta name="theme-color" media="(prefers-color-scheme: dark)" content="#2b3035">
<script src="bs-theme-preboot.js"></script>
<!-- stylesheets come after this line -->
```

The order matters:

- **`<base href>` first.** The script's `src` is relative. Before `<base>`, a deep link such as
  `/po/car/123` resolves it to `/po/car/bs-theme-preboot.js`, which gets the SPA fallback (HTML), and
  the page paints light.
- **The `bs-theme-default-mode` meta before the script.** The script reads it with `querySelector`
  while `<head>` is still parsing, so a meta after it is not there yet. It is the default for a user
  who has chosen nothing (`auto`, `light` or `dark`).
- **The script before every stylesheet, with no `async` or `defer`.** It sets
  `<html data-bs-theme>` synchronously, so the first paint is already in the right scheme. A late
  script means a light flash on every load.
- **`color-scheme`** themes the browser's own UI (scrollbars, form controls) before any CSS loads.
  **`theme-color`** colours the mobile address bar; the values match the shell's chrome.

**Nothing in `app.config.ts`.** `spark-shell` injects `BsThemeService` itself. `provideBsTheme()` is
needed only to set `cookieDomain` (§3).

## 2. How the modes behave

The toggle is one button that cycles **Auto → Light → Dark**. Its label names the next action
("Switch to light theme"); the current mode is announced to screen readers.

| The user… | What happens |
|---|---|
| has chosen nothing, or chose **Auto** | The page follows `prefers-color-scheme` **live**: switching the OS to dark re-themes the open page with no reload. |
| chose **Light** or **Dark** | The choice is **sticky**. An OS change does not override it. Choosing Auto hands control back. |
| has the app open in several tabs | A choice in one tab re-themes the others at once (`BroadcastChannel`); a new tab reads the cookie. |

Sticky was chosen over "the last action wins" (an OS change after an explicit choice would flip it
back) because it is simpler and matches GitHub and MDN.

## 3. The cookie

The choice is stored in the **`bs-theme-mode`** cookie (`auto`, `light` or `dark`), not
`localStorage`, so the server can read it (§6). Attributes, as written by the toggle and measured
in DemoApp:

- `Path=/`, `SameSite=Lax`, a long `Max-Age`, renewed on every choice.
- `Secure` on `https:` only. Over plain `http:` the cookie is written without it, so local http
  development still works.
- **Host-only by default**: no `Domain`, so it belongs to the exact host that set it.

To share the choice across subdomains (say `app.example.com` and `id.example.com`), set a domain:

```ts
import { provideBsTheme } from '@mintplayer/ng-bootstrap/theming';

export const appConfig: ApplicationConfig = {
  providers: [provideBsTheme({ cookieDomain: 'example.com' })],
};
```

With a domain set, a choice first deletes any host-only `bs-theme-mode` on the current host, so an
old host-only value cannot shadow the shared one.

A value that is not one of the three modes is ignored, everywhere.

## 4. spark-shell's toggle

`<spark-shell>` renders `<bs-theme-toggle>` in its topbar, next to the trailing slot, with Spark's
translated labels (`theme.*` keys). It stays when an app replaces the trailing slot (for example with
`spark-auth-bar`).

```html
<spark-shell title="My app" [themeToggle]="false"> … </spark-shell>
```

`[themeToggle]="false"` hides it. Auto still follows the OS live, because the shell keeps the theme
service running; the app may then place its own `<bs-theme-toggle [modes]="…">` elsewhere.

## 5. Changing the colours

### The shell: `--spark-shell-*` tokens

The shell's chrome is drawn from six custom properties, declared per scheme from Bootstrap tokens:

| Token | Default |
|---|---|
| `--spark-shell-topbar-bg`, `--spark-shell-sidebar-bg` | `var(--bs-tertiary-bg)` |
| `--spark-shell-topbar-color`, `--spark-shell-sidebar-color` | `var(--bs-body-color)` |
| `--spark-shell-main-bg` | `var(--bs-body-bg)` |
| `--spark-shell-main-color` | `var(--bs-body-color)` |

Override them per scheme in the app's global stylesheet:

```css
[data-bs-theme=light] spark-shell { --spark-shell-sidebar-bg: #1e293b; --spark-shell-sidebar-color: #fff; }
[data-bs-theme=dark]  spark-shell { --spark-shell-sidebar-bg: #0f172a; }
```

The sticky **action bar** above a form or a list (`.spark-actionbar`) paints
`--spark-shell-main-bg`, opaque, with a bottom border: it sits on the page surface and never floats
over content, so changing the main background changes it too.

The old `sidebarTheme` input is gone; the tokens replace it. See also
[Program Units & spark-shell](guide-program-units.md).

### Everything else: Bootstrap's `--bs-*` custom properties

The dark palette is Bootstrap 5.3's `_variables-dark.scss`, as shipped. It is already in every app's
global CSS as a `[data-bs-theme=dark] { --bs-body-bg: …; … }` block. To change it, override the
**CSS custom properties**, per scheme:

```css
[data-bs-theme=dark] {
  --bs-body-bg: #181a1b;
  --bs-tertiary-bg: #202324;
  --bs-primary-bg-subtle: #10264a;
}
```

**Do not override the Sass `$*-dark` variables** (`$body-bg-dark` and friends). They change only
what the app itself compiles. ng-bootstrap's component styles are compiled when the library is built,
with the default values, so a Sass override never reaches them and the app ends up with two dark
palettes. A custom property set on `<html>` reaches both the global CSS and every component, because
the components read `var(--bs-*)` (PRD §1.4, spike S1). The exception is a component-level
`--bs-<component>-*` property declared with a literal on the component's own element; that needs a
selector at least as specific as the component's.

Write your own colours as `var(--bs-*)` too (`--bs-secondary-bg`, `--bs-*-bg-subtle`,
`bg-body-tertiary`), never as literals or `bg-light`, so they follow the scheme.

## 6. The identity-provider pages

The identity provider's server-rendered pages under `/connect/*` (login, two-factor, consent and
connected applications; the CSS is shared in `Endpoints/ConnectPageTheme.cs`) have no script. Each endpoint reads the `bs-theme-mode` cookie: `light` or `dark` is rendered as
`<html data-bs-theme="…">`. For `auto`, no cookie, or an invalid value, nothing is rendered, and the
pages' CSS falls back to `@media (prefers-color-scheme: dark)` scoped to
`:root:not([data-bs-theme=light])`, which also follows a live OS change. The cookie is host-only, so
these pages see it only when they are on the SPA's host (or under a shared `cookieDomain`);
otherwise they follow the OS.

## 7. Exceptions and known limits

- **CodeCoverage's SVG badges do not theme.** A badge is embedded in READMEs on other sites, where
  the page's theme is unknown, so it keeps its fixed colours. `BadgeRendererTests` pins that the
  output has no `prefers-color-scheme`, `<style` or `currentColor`.
- **Shadow-DOM controls rely on CSS style queries.** The form-select caret and the form-switch knob
  inside ng-bootstrap's web components switch with `@container style(--mp-color-mode: dark)`. An
  engine without custom-property style queries keeps the light caret and knob; the rest of the page
  is unaffected. See the ng-bootstrap CHANGELOG (22.20.0) for the engines measured.
