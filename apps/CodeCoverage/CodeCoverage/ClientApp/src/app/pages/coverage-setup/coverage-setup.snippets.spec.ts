import { describe, expect, it } from 'vitest';
import { coverageSetupGuides } from './coverage-setup.snippets';

/*
 * FR13: the guide hands out the same workflows the repository page's panel did. The .NET example is pinned word for
 * word as the panel rendered it for this base URL; the others are held to the same invariants the panel's spec checked.
 */
const DOTNET_AS_THE_PANEL_RENDERED_IT = `name: CI
on:
  push:
    branches: [main]
  pull_request:

permissions:
  contents: read
  id-token: write   # tokenless upload via OIDC

jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 9.0.x
      - run: dotnet test --collect:"XPlat Code Coverage"
      - name: Upload coverage
        uses: MintPlayer/MintPlayer.Spark/apps/CodeCoverage/action@coverage-upload-v1
        with:
          url: https://cov.example.test
          use-oidc: true
          finish: true`;

describe('coverageSetupGuides', () => {
  const guides = coverageSetupGuides('https://cov.example.test');
  const examples = guides[0].examples;

  it('has the one GitHub guide, with an example per ecosystem', () => {
    expect(guides.map(g => g.provider)).toEqual(['github']);
    expect(guides[0].intro).toContain('<code>.github/workflows/ci.yml</code>');
    expect(examples.map(e => e.label)).toEqual(['.NET', 'Node.js', 'Angular', 'React', 'Python', 'Java', 'Nx']);
  });

  it('produces the .NET workflow exactly as the panel did', () => {
    expect(examples.find(e => e.key === 'dotnet')!.code).toBe(DOTNET_AS_THE_PANEL_RENDERED_IT);
  });

  it('builds every workflow against the base URL and the pinned action tag', () => {
    for (const { code } of examples) {
      expect(code).toContain('url: https://cov.example.test');
      expect(code).toContain('uses: MintPlayer/MintPlayer.Spark/apps/CodeCoverage/action@coverage-upload-v1');
      expect(code).toContain('id-token: write');
      expect(code).toContain('finish: true');
    }
    expect(examples.find(e => e.key === 'java')!.code).toContain("files: '**/jacoco.xml'");
    expect(examples.find(e => e.key === 'nx')!.code).toContain("files: 'coverage/**/lcov.info'");
  });

  it('gives Nx its per-project config', () => {
    const config = examples.find(e => e.key === 'nx')!.config!;
    expect(config.language).toBe('ts');
    expect(config.code).toContain("reportsDirectory: '../../coverage/libs/my-lib'");
    expect(config.note).toContain('declare that folder as the');
  });
});
