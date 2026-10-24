/*
 * The coverage-uploads guide's examples (/coverage/setup). They moved here verbatim from the repository page's
 * "Set up coverage uploads" panel (PR size labels PRD D10), as a pure function of the server's base URL, so the snippets
 * spec can hold them to what the panel produced.
 */

/**
 * One forge's setup guide: where the file goes, how to say it, and the per-language examples.
 *
 * ⚠️ The intro is part of the guide rather than fixed copy above the tabs, because almost every
 * word of it is forge-specific — the file path, what a stored credential is called ("repository
 * secret" on GitHub, "CI/CD variable" on GitLab, "repository variable" on Bitbucket) and whether a
 * tokenless upload is possible at all. Leaving it outside would make the first GitLab snippet
 * arrive under instructions describing GitHub.
 */
export interface ForgeSetupGuide {
  /** Canonical forge code, matching EForgeProvider's spelling: github | gitlab | bitbucket. */
  provider: string;
  /** What to call it in the tab header. */
  label: string;
  /** Intro paragraph, rendered as HTML so it can mark up file names and inputs. */
  intro: string;
  examples: WorkflowExample[];
}

export interface WorkflowExample {
  key: string;
  label: string;
  note: string;
  code: string;
  /** Optional per-project configuration shown above the workflow. */
  config?: { note: string; code: string; language: string };
}

/**
 * Setup guides by forge. GitHub is the only populated one in stage 1 — the structure exists so
 * that adding GitLab is adding an entry here rather than reshaping the page.
 */
export function coverageSetupGuides(url: string): ForgeSetupGuide[] {
  return [
    {
      provider: 'github',
      label: 'GitHub Actions',
      intro:
        'Add a workflow like this to <code>.github/workflows/ci.yml</code>. Public repositories '
        + 'upload tokenless via OIDC (the <code>id-token: write</code> permission); for a private '
        + 'repository, create an upload token on the account page, store it as a repository secret '
        + 'and replace the <code>use-oidc</code> line with a <code>token</code> input.',
      examples: gitHubExamples(url),
    },
  ];
}

/** Example CI workflows per ecosystem, built against this deployment's URL. */
function gitHubExamples(url: string): WorkflowExample[] {
  // Pinned to the moving major tag, never to a branch: `master` of that
  // monorepo moves many times a day for unrelated reasons, while
  // coverage-upload-v1 only ever moves to a commit carrying a v1-compatible
  // bundle. The previous value here — MintPlayer/CodeCoverage/action@master —
  // pointed at a repository that is now archived, so every repo onboarded
  // through this guide was handed a ref that can never be rebuilt.
  const upload = (extra = '') => `      - name: Upload coverage
        uses: MintPlayer/MintPlayer.Spark/apps/CodeCoverage/action@coverage-upload-v1
        with:
          url: ${url}
          use-oidc: true${extra}
          finish: true`;
  const header = (testJob: string) => `name: CI
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
${testJob}`;

  return [
    {
      key: 'dotnet',
      label: '.NET',
      note: 'Coverlet ships with the xunit/mstest templates; --collect produces a Cobertura report the action auto-detects.',
      code: header(`      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 9.0.x
      - run: dotnet test --collect:"XPlat Code Coverage"
${upload()}`),
    },
    {
      key: 'node',
      label: 'Node.js',
      note: 'Jest writes coverage/lcov.info when run with --coverage; lcov is auto-detected.',
      code: header(`      - uses: actions/setup-node@v4
        with:
          node-version: 24
      - run: npm ci
      - run: npx jest --coverage
${upload()}`),
    },
    {
      key: 'angular',
      label: 'Angular',
      note: 'ng test --code-coverage emits coverage/<project>/lcov.info via karma-coverage.',
      code: header(`      - uses: actions/setup-node@v4
        with:
          node-version: 24
      - run: npm ci
      - run: npx ng test --watch=false --code-coverage --browsers=ChromeHeadless
${upload()}`),
    },
    {
      key: 'react',
      label: 'React',
      note: 'Vitest with the v8 provider writes an lcov report; for CRA/jest use "npm test -- --coverage --watchAll=false" instead.',
      code: header(`      - uses: actions/setup-node@v4
        with:
          node-version: 24
      - run: npm ci
      - run: npx vitest run --coverage --coverage.reporter=lcov
${upload()}`),
    },
    {
      key: 'python',
      label: 'Python',
      note: 'pytest-cov with --cov-report=xml produces a Cobertura-style coverage.xml.',
      code: header(`      - uses: actions/setup-python@v5
        with:
          python-version: "3.13"
      - run: pip install -r requirements.txt pytest pytest-cov
      - run: pytest --cov --cov-report=xml
${upload()}`),
    },
    {
      key: 'java',
      label: 'Java',
      note: 'The JaCoCo Maven plugin writes target/site/jacoco/jacoco.xml during verify.',
      code: header(`      - uses: actions/setup-java@v4
        with:
          distribution: temurin
          java-version: "21"
      - run: mvn -B verify
${upload(`
          files: '**/jacoco.xml'`)}`),
    },
    {
      key: 'nx',
      label: 'Nx',
      note: 'Prefer run-many over "nx affected" for the coverage run: unaffected projects emit no report, '
        + 'so an affected upload reads as a coverage drop for everything untouched. The --coverage flag '
        + 'forwards to vitest/jest through every Nx target shape (no "--" separator) — including non-JS '
        + 'targets, where it breaks the command (a dotnet test target chokes on it: --exclude those). '
        + 'And run it on the plain test target, not atomized test-ci targets — those run one spec file '
        + 'each into the same directory and overwrite each other\'s report.',
      config: {
        note: 'Per project, emit lcov into a stable workspace-level folder AND declare that folder as the '
          + 'target\'s outputs — otherwise a cache-restored test run produces no report to upload. '
          + 'Vitest needs both lines below (lcov is not a vitest default); Jest projects only need '
          + '"coverageDirectory" (lcov is a Jest default).',
        language: 'ts',
        code: `// libs/my-lib/vitest.config.ts
export default defineConfig({
  test: {
    coverage: {
      provider: 'v8',
      reporter: ['text', 'lcov'],
      reportsDirectory: '../../coverage/libs/my-lib',
    },
  },
});

// libs/my-lib/project.json — lets Nx restore reports on cache hits
//   "test": {
//     "outputs": ["{workspaceRoot}/coverage/{projectRoot}"],
//     ...
//   }`,
      },
      code: header(`      - uses: actions/setup-node@v4
        with:
          node-version: 24
          cache: npm
      - run: npm ci
      - run: npx nx run-many -t test --coverage
${upload(`
          files: 'coverage/**/lcov.info'`)}`),
    },
  ];
}
