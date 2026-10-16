Synthetic, produced by the S14 spike against a LOCAL registry/feed only (never published publicly).
Packages were malicious-by-design (sentinel-writing hooks). These are terminal transcripts of real
resolver CONFLICT output, captured to serve as parser fixtures for the dependency-update bot.

## eresolve-peer-conflict.txt
Command: npm install peerhost@1.0.0 --save-exact --ignore-scripts --package-lock-only
Setup: root has evil-pkg@2.0.0; peerhost@1.0.0 declares peer evil-pkg@^1.0.0.
Result: npm error code ERESOLVE, "unable to resolve dependency tree". npm 11.12.1.

## eresolve-overriding-peer-warning.txt
Command: npm install evil-pkg@2.0.0 --save-exact --ignore-scripts --package-lock-only
Setup: root had evil-pkg@1.0.0 + peerhost@1.0.0 (peer ^1.0.0); bumping to 2.0.0.
Result: WARN ERESOLVE overriding peer dependency (non-fatal warning form).
