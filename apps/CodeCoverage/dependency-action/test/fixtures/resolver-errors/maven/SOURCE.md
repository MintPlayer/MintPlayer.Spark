Synthetic, produced by the S14 spike against a LOCAL registry/feed only (never published publicly).
Packages were malicious-by-design (sentinel-writing hooks). These are terminal transcripts of real
resolver CONFLICT output, captured to serve as parser fixtures for the dependency-update bot.

## missing-version.txt
Command: mvn dependency:get -Dartifact=com.evil:dep:9.9.9 (local repo has 1.0.0/1.1.0). maven 3.9.16.
Result: DependencyResolutionException: com.evil:dep:jar:9.9.9 (absent): Could not find artifact.
