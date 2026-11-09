# Source: #606 S4 (PHP, Ruby and Rust platform locations)

Real files from public repositories, captured 2026-11-09 with `curl https://raw.githubusercontent.com/<owner>/<repo>/HEAD/<path>`:

| Fixture | Repository | What it shows |
|---|---|---|
| `laravel/composer.json` | laravel/laravel | `require.php` `^8.3`: a range, reported, never edited |
| `symfony-demo/composer.json` | symfony/demo | `require.php` `>=8.4` (a range) and `config.platform.php` `8.4.1` (exact, editable) |
| `mastodon/.ruby-version` | mastodon/mastodon | `4.0.9`: a version file |
| `mastodon/Gemfile` | mastodon/mastodon | `ruby file: '.ruby-version'`: no version of its own |
| `discourse/Gemfile` | discourse/discourse | `ruby "~> 4.0.0"`: a range, reported |
| `rubocop/rubocop.gemspec` | rubocop/rubocop | `required_ruby_version = '>= 2.7.0'`: a gem's minimum, reported (widened by hand only) |
| `rubocop/.github/workflows/rubocop.yml` | rubocop/rubocop | `ruby/setup-ruby` `ruby-version:` as a matrix expression, `'jruby'`, `ruby` and `2.7` |
| `ruff/rust-toolchain.toml` | astral-sh/ruff | `[toolchain] channel = "1.99.0"`: the pinned toolchain, editable |
| `ruff/Cargo.toml` | astral-sh/ruff | `rust-version = "1.97"`: the MSRV, reported, never raised (D11) |
| `laravel-framework/.github/workflows/tests.yml` | laravel/framework | `php-version:` passed to a local composite action (`./.github/actions/setup-php-project`), not `shivammathur/setup-php`: not a location |

Ranges and minimums (`require.php`, `Gemfile` `ruby "~> …"`, `required_ruby_version`, `rust-version`) are reported
and never edited, as `requires-python` has been since #419: narrowing or raising one breaks a library's consumers
(PRD §7 "Constraint widening").
