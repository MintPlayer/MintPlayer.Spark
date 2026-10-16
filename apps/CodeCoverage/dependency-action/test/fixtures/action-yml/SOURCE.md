# Source: action.yml pairs (R15 interface-diff fixtures)

- Tool: `gh` CLI, GitHub REST contents API with the raw media type
- Captured: 2026-10-15 by `spikes/s12/capture-action-yml.sh`
- Command per file: `gh api -H "Accept: application/vnd.github.raw" "repos/<owner>/<repo>/contents/action.yml?ref=<tag>"`
  (falls back to `action.yaml`); the tag's commit sha comes from `gh api repos/<owner>/<repo>/commits/<tag> --jq .sha`
- `index.tsv` holds `repo, tag, commit sha, file name` for each capture. The tags are MOVING major tags
  (v4 = the latest v4.x at capture time), so re-capture by the recorded sha, not the tag.

| Pair | runs.using | Inputs | Outputs |
|---|---|---|---|
| actions/checkout v4 → v5 | node20 → node24 | identical | identical (ref, commit) |
| actions/upload-artifact v3 → v4 | node16 → node20 | + compression-level, overwrite | + artifact-id, artifact-url, artifact-digest |
| actions/setup-node v4 → v5 | node20 → node24 | + package-manager-cache | identical |

None of the three pairs removes or renames an input or output: the interface diff alone flags only the runtime
change. upload-artifact v4's real break (artifacts immutable, same-name uploads in one run conflict) is invisible
in `action.yml`.
