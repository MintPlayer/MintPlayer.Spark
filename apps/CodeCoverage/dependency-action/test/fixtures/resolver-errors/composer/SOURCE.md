Synthetic, produced by the S14 spike against a LOCAL registry/feed only (never published publicly).
Packages were malicious-by-design (sentinel-writing hooks). These are terminal transcripts of real
resolver CONFLICT output, captured to serve as parser fixtures for the dependency-update bot.

## conflict-no-matching-version.txt
Command: composer update --no-scripts --no-plugins (require evil/plugin 9.9.9; artifact repo has 1.0.0/1.1.0).
Env: COMPOSER_DISABLE_NETWORK=1, packagist disabled. composer 2.x.
Result: Problem 1 - Root composer.json requires evil/plugin 9.9.9 ... found evil/plugin[1.0.0, 1.1.0] but it does not match the constraint.
