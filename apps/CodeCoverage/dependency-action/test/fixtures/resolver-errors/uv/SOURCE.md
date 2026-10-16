Synthetic, produced by the S14 spike against a LOCAL registry/feed only (never published publicly).
Packages were malicious-by-design (sentinel-writing hooks). These are terminal transcripts of real
resolver CONFLICT output, captured to serve as parser fixtures for the dependency-update bot.

## no-solution-missing-version.txt
Command: uv lock --no-index --find-links <local> (pin evilpkg==9.9.9, only 1.0.0/1.1.0 exist). uv 0.13.1.
Result: error: No solution found when resolving dependencies.

## no-build-no-wheel.txt
Command: uv lock --no-index --find-links <local> --no-build (evilpkg ships only an sdist).
Result: No solution found ... has no usable wheels ... building from source is disabled (--no-build).
