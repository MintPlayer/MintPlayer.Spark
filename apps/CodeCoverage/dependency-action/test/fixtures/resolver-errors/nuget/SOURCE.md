Synthetic, produced by the S14 spike against a LOCAL registry/feed only (never published publicly).
Packages were malicious-by-design (sentinel-writing hooks). These are terminal transcripts of real
resolver CONFLICT output, captured to serve as parser fixtures for the dependency-update bot.

## NU1102-missing-version.txt
Command: dotnet restore (PackageReference EvilNuget 9.9.9 against a local folder feed with <clear/>).
Result: error NU1102: Unable to find package EvilNuget with version (>= 9.9.9). dotnet SDK 11.0.100-rc.1.

## NU1605-downgrade.txt
Command: dotnet restore. Project directly references EvilNuget 1.0.0 and EvilParent 2.0.0,
which depends on EvilNuget >= 2.0.0 -> downgrade. (TreatWarningsAsErrors surfaced NU1605 as error.)
Result: error NU1605: Detected package downgrade: EvilNuget from 2.0.0 to 1.0.0.
