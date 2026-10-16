Synthetic, produced by the S14 spike against a LOCAL registry/feed only (never published publicly).
Packages were malicious-by-design (sentinel-writing hooks). These are terminal transcripts of real
resolver CONFLICT output, captured to serve as parser fixtures for the dependency-update bot.

## unmet-peer-warning.txt
Command: pnpm add peerhost@1.0.0 --save-exact --lockfile-only --ignore-scripts --config.strict-peer-dependencies=true
Setup: root has evil-pkg@2.0.0; peerhost needs peer evil-pkg@^1.0.0. pnpm 10.18.0.
Result: WARN Issues with peer dependencies found / unmet peer evil-pkg@^1.0.0: found 2.0.0.
