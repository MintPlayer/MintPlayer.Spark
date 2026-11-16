# Source: S4 golden (platform-locations-PRD §7 S4)

- Repository: MintPlayer/MintPlayer.Dotnet.Templates, commit `6f039d1ffe0df7ba0737a5b60865fef126f2ff8a` (HEAD on 2026-11-14).
- `before/<path>`: the file at that commit, unchanged, for every file that carries a .NET or Node location: the
  `*.csproj.in` project templates, the package project, `eng/Versions.props` (the TFM chain), the `Directory.Build.*`
  that import it, the template's `template.json` and Dockerfile, and `publish-master.yml`.
- The project templates (`*.csproj.in`) sit next to `content/`, not under the template's folder: the package's
  `GenerateContent.targets` writes them into `content/` at pack time. This is the shape the golden pins.
