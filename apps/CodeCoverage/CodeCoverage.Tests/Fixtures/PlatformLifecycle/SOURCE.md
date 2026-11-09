# Source: platform lifecycle feeds

Real responses, saved with `curl -sS -o <file> <url>` (no credentials). Frozen point-in-time answers: tests treat them as
fixtures, never as "today".

## #568 M3 (captured 2026-11-05)

| File | URL |
|---|---|
| `dotnet-releases-index.json` | https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json |
| `endoflife-angular.json` | https://endoflife.date/api/v1/products/angular |
| `endoflife-nodejs.json` | https://endoflife.date/api/v1/products/nodejs |
| `npm-dist-tags-angular-core.json` | https://registry.npmjs.org/-/package/@angular/core/dist-tags |
| `npm-dist-tags-nestjs-core.json` | https://registry.npmjs.org/-/package/@nestjs/core/dist-tags |

The collector asked for `…/-/package/@angular/core` without `/dist-tags` until #606; npm answers that with 404
(checked 2026-11-09), so production never stored a framework preview major. The tests now stub the real path.

`npm-dist-tags-angular-core-preview.json` is the one derived fixture: the real Angular tags with `next` set to
`23.0.0-next.0`, because no framework had a preview major that day.

## #606 S6 (captured 2026-11-09)

| File | URL |
|---|---|
| `endoflife-eclipse-temurin.json` | https://endoflife.date/api/v1/products/eclipse-temurin |
| `endoflife-go.json` | https://endoflife.date/api/v1/products/go |
| `endoflife-php.json` | https://endoflife.date/api/v1/products/php |
| `endoflife-python.json` | https://endoflife.date/api/v1/products/python |
| `endoflife-ruby.json` | https://endoflife.date/api/v1/products/ruby |
| `endoflife-rust.json` | https://endoflife.date/api/v1/products/rust |
| `node-rc-index.json` | https://nodejs.org/download/rc/index.json |
| `python-prereleases.json` | https://www.python.org/api/v2/downloads/release/?pre_release=true&is_published=true |
| `php-release-candidates.json` | https://www.php.net/release-candidates.php?format=json (now a 302 to `pre-release-builds.php?format=json`, same JSON; the collector asks for the new URL) |
| `ruby-releases.yml` | https://raw.githubusercontent.com/ruby/www.ruby-lang.org/master/_data/releases.yml |
| `rust-channel-beta.toml` | https://static.rust-lang.org/dist/channel-rust-beta.toml |

What these captures say, and the tests rely on:

- .NET: 11.0 is `go-live` (`11.0.0-rc.3`, SDK `11.0.100-rc.3.26522.148`), 10.0 the active LTS (SDK `10.0.403`).
- Newest released channels: node 26 (LTS), python 3.15, php 8.5, ruby 4.0, rust 1.99, go 1.27.
- Previews above them: python `3.16.0a1`, php `8.6.0RC5`, ruby `4.1.0-preview1`, rust `1.100.0-beta.13` (manifest date
  2026-11-08). Node's newest release candidate is `v26.4.0-rc.0`, below its released 26: no node preview.
- php.net writes `"enabled": false` on the withdrawn 8.2/8.3 entries and no `enabled` at all on the published ones.
- endoflife.date flags LTS releases for nodejs and eclipse-temurin only.
