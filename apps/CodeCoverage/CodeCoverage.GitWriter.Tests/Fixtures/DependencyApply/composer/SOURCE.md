# Source: S13 apply spike, composer

Generated 2026-10-15 by `spikes/s13/run-composer.sh (composer:2 image)` (sample repo built under `spikes/s13/work/`, one base commit, one pinned bump per case).
Each `*.diff` is `git diff` (untracked files included via `git add -N`) after the case's command, against the base commit.
Timings: `spikes/s13/logs/_timings.txt`; raw tool output: `spikes/s13/logs/<case>.log`.

Composer >= 2.9 blocks versions with security advisories by default; the sample sets config.policy.advisories.block=false to lock an old base.
- native-require: `composer require monolog/monolog:2.9.2 --no-install --no-scripts --no-plugins` - pure.
- textedit-update-pkg: text edit + `composer update monolog/monolog --no-install --no-scripts --no-plugins` - identical. RECOMMENDED.
- textedit-range-with: range ^7.5 -> ^7.8 + `composer update guzzlehttp/guzzle --with guzzlehttp/guzzle:7.8.0` - pins the lock exactly.
- conflict.log: exit 2, prose 'Problem 1 ... requires psr/log ^2.0 || ^3.0 ... conflicts with your root composer.json require'.
