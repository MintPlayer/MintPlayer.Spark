# Program Units — the server-driven menu, composed pages, and `spark-shell`

The application menu is data, not markup. `App_Data/programUnits.json` declares it, the server
filters it per caller against `security.json`, and `@mintplayer/ng-spark/shell` renders it.
**Consumers write zero router links for navigation** — every group, unit, icon, label and link
comes from the file, and the menu re-fetches itself when the user signs in or out.

## programUnits.json

```jsonc
{
  "programUnitGroups": [
    {
      "id": "…guid…",
      "name": { "en": "Fleet Management", "nl": "Wagenparkbeheer" },
      "icon": "bi-truck",
      "order": 1,
      "programUnits": [
        {
          // A grid: opens /query/{alias ?? queryId}. Visible iff the caller holds Query on the
          // entity type behind the query.
          "id": "…guid…", "name": { "en": "Cars" }, "icon": "bi-car-front-fill",
          "type": "query", "queryId": "…guid…", "alias": "cars", "order": 1
        },
        {
          // A page: opens /po/{alias ?? typeId}/{objectId}. Visible iff the caller holds Read
          // on the type. Without objectId it opens the type's default list instead.
          "id": "…guid…", "name": { "en": "Start" }, "icon": "bi-house-door",
          "type": "persistentObject", "persistentObjectId": "…guid…",
          "alias": "startpage", "objectId": "start", "order": 2
        },
        {
          // An external link: a plain anchor (new tab), always visible.
          "id": "…guid…", "name": { "en": "Status page" }, "icon": "bi-activity",
          "type": "url", "url": "https://status.example.com", "order": 3
        }
      ]
    }
  ]
}
```

Semantics:

| `type` | required field | visible iff | opens |
|---|---|---|---|
| `query` | `queryId` | `Query` right on the entity type | `/query/{alias ?? queryId}` |
| `persistentObject` | `persistentObjectId` | `Read` right on the type | `/po/{alias ?? typeId}` (list), or `/po/…/{objectId}` when `objectId` is set |
| `url` | `url` | always | the external address, new tab |

The loader (`ProgramUnitsLoader`) canonicalizes `type` casing and validates these combinations
at load time — an unknown type or a missing required field **throws**
(`SparkProgramUnitsConfigurationException`) rather than silently dropping the unit, because a
silently missing menu entry reads exactly like an authorization problem. A missing
`programUnits.json` is fine: no menu is a valid choice.

There is deliberately **no visibility data in the file**. What a caller sees is derived from
`security.json` rights on each unit's target; groups whose units all filtered away are not sent
at all. `GET /spark/program-units` is therefore caller-scoped — which is why the client re-fetches
it on sign-in/out.

## Composed pages: a menu entry that opens code, not a document

A `persistentObject` unit with an `objectId` can target a type that has **no documents and no
CLR class at all** — a start page, a dashboard, a per-user landing page. The recipe (DemoApp's
`StartPage` is the worked example):

1. **A hand-authored model file** — `App_Data/Model/StartPage.json` declaring the attributes
   (read-only), tabs/groups, breadcrumb — and **no `clrType`**. That absence is what makes the
   type virtual: everything document-shaped (load, query, save) 404s for it. Run
   `--spark-synchronize-model` afterwards so `modelHashes.json` covers the file; synchronize
   preserves hand-authored files.

2. **The load hook** — a plain class named `{Name}Actions`, resolved by name exactly like
   entity Actions classes; no base class. The hook has the one signature every actions class
   has — id in, page out — and, since there is no document, the class scaffolds its object from
   the model via `IManager` (the same idiom dialog POs use), fills **only the attribute
   values**, and returns it. The framework squares the envelope — each only when the hook didn't
   set it itself: `Id` defaults to the requested id, `Breadcrumb` (the page title) renders from
   the model file's `breadcrumb` template over the values just filled, and `can` is forced
   read-only:

   ```csharp
   public partial class StartPageActions
   {
       [Inject] private readonly IManager manager;
       [Inject] private readonly IAsyncDocumentSession session;

       public async Task<PersistentObject?> OnLoadAsync(string id, PersistentObject? parent)
       {
           var obj = manager.GetPersistentObject("StartPage");   // scaffold: all attributes, null values
           obj["Welcome"].Value = "Hello!";
           obj["PeopleCount"].Value = await session.Query<Person>().CountAsync();
           return obj;                                           // null ⇒ 404
       }
   }
   ```

   The signature is checked reflectively and loudly: a method named `OnLoadAsync` with any other
   shape throws at first load rather than silently 404ing. A virtual type with no Actions class
   (or none with the hook) has no page — 404.

3. **A grant** — `Read/StartPage` in `security.json`. No grant, no page and no menu entry.

4. **The unit** — `type: "persistentObject"`, the type's id, and any stable `objectId` string
   (`"start"`); the page is free to ignore it.

The same JSON-only shape serves dialog/popup POs: declare the model file, scaffold with
`IManager.GetPersistentObject(...)` inside a custom action, and hand it to a retry action —
no class needed there either (Fleet's `ConfirmDeleteCar` is the worked example).

What the framework does with a composed object: it is served after the type-level `Read` check
and **instead of** the entity pipeline — no document load, no collection guard, no row security
(the hook hand-picks every value it exposes and is the same authority those guards defer to),
no Etag. `can.edit`/`can.delete` are forced false unless the hook sets them, so the generic
detail page renders it read-only with the breadcrumb as heading. Anything interactive on such a
page is a custom action, which carries its own authorization.

## `spark-shell` — the application frame

`@mintplayer/ng-spark/shell` ships the whole chrome: topbar + sidebar + main over ng-bootstrap's
`bs-shell` (whose web component owns all responsive behavior — breakpoints, the overlay drawer,
dismiss-on-navigate). The minimal app shell is:

```html
<spark-shell title="My App">
  <router-outlet />   <!-- default content = the main region -->
</spark-shell>
```

Everything else is a **slot** — a structural directive whose template replaces one region; an
omitted slot renders its default:

| Slot | Region | Default |
|---|---|---|
| `*sparkShellTopbarStart` | topbar, left | the sidebar toggler |
| `*sparkShellTopbarEnd` | topbar, right | `<spark-language-selector />` |
| `*sparkShellSidebarHeader` | sidebar, top | `<h5>{{ title }}</h5>` |
| `*sparkShellSidebarTop` | between header and menu | — |
| `*sparkShellSidebarFooter` | sidebar, bottom | — |
| `*sparkShellMainHeader` | main, above the content | — |

```html
<spark-shell title="Spark Demo">
  <!-- auth is app territory: ng-spark cannot depend on ng-spark-auth, so the auth bar is
       always slotted. Include the language selector again if you still want it. -->
  <ng-container *sparkShellTopbarEnd>
    <spark-language-selector />
    <spark-auth-bar />
  </ng-container>

  <!-- an app-specific page that isn't in the model, hence not a program unit -->
  <a *sparkShellSidebarTop routerLink="/reports" class="d-block px-3 py-2 text-decoration-none nav-link">Reports</a>

  <router-outlet />
</spark-shell>
```

Notes:

- **The menu is not a slot.** If you're writing unit anchors in a slot, add units to
  `programUnits.json` instead.
- **Extra accordion tabs** — for a page the model cannot describe — use `*sparkShellTab`, which
  contributes a header and a body and lets the menu build the tab:

  ```html
  <ng-container *sparkShellTab="'Component demos'; icon: 'palette'">
    <a routerLink="/query-slots" routerLinkActive="active" class="d-block px-3 py-2 nav-link">Query card slots</a>
  </ng-container>
  ```

  Don't declare your own `<bs-accordion>` in a sidebar slot: single-open is enforced *per
  accordion element* — over the children it owns and over `<details name>`, whose grouping cannot
  cross a shadow root — so a second accordion is a second exclusivity group, and its tab would
  stay open while a generated group opens. `*sparkShellTab` exists so the tab element is created
  by the menu, inside the one accordion. (`sparkShellTabHeader` takes a `TemplateRef` if the
  header needs its own markup; `sidebarTabs` is the same thing as a data input.)
- Every slot also exists as a `TemplateRef` input (`topbarEndTemplate`, …) for hosts that can't
  use content projection.
- Inputs: `title`, `breakpoint` (default `md`), `sidebarTheme` (`'dark' | 'light' | null`,
  default `'dark'`), `reloadToken` (forwarded to the menu).
- Theming: override `--spark-shell-topbar-bg`, `--spark-shell-sidebar-bg`,
  `--spark-shell-main-bg` on the `<spark-shell>` element. `sidebarTheme` flips the sidebar's
  `data-bs-theme`, which is what recolors the accordion internals across the web component's
  shadow boundary.

### Sign-in/out re-fetch

The menu tracks the optional `SPARK_AUTH_STATE` token — a `Signal<unknown>` that changes when
the user changes. `provideSparkAuth()` supplies it automatically from `SparkAuthService.user`,
so apps using `@mintplayer/ng-spark-auth` get the re-fetch with no wiring. An app with its own
auth stack provides its own signal:

```ts
{ provide: SPARK_AUTH_STATE, useFactory: () => inject(MyAuthService).currentUser }
```

Without a provider the menu is fetched once; `reloadToken` / `reload()` are the manual hatches.

### Without the shell

`<spark-program-units />` is exported standalone for a host that owns its own layout — same
menu, same auth re-fetch, no frame.
