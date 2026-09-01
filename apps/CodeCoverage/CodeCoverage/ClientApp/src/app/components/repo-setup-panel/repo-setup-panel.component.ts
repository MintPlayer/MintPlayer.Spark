import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { BsCardComponent, BsCardHeaderComponent, BsCardBodyComponent } from '@mintplayer/ng-bootstrap/card';
import { BsTabControlComponent, BsTabPageComponent, BsTabPageHeaderDirective } from '@mintplayer/ng-bootstrap/tab-control';
import { BsCodeSnippetComponent } from '@mintplayer/ng-bootstrap/code-snippet';

interface WorkflowExample {
  key: string;
  label: string;
  note: string;
  code: string;
  /** Optional per-project configuration shown above the workflow. */
  config?: { note: string; code: string; language: string };
}

/**
 * "Set up coverage uploads" card (per-ecosystem CI workflow examples), shared
 * by the vanity repo page and the generic /po Repository detail page.
 */
@Component({
  selector: 'app-repo-setup-panel',
  imports: [BsCardComponent, BsCardHeaderComponent, BsCardBodyComponent, BsTabControlComponent, BsTabPageComponent, BsTabPageHeaderDirective, BsCodeSnippetComponent],
  template: `
    <bs-card class="mt-3 d-block">
      <bs-card-header><i class="bi bi-rocket-takeoff"></i> Set up coverage uploads</bs-card-header>
      <bs-card-body>
        <p class="text-muted small">
          Add a workflow like this to <code>.github/workflows/ci.yml</code>. Public repositories upload
          tokenless via OIDC (the <code>id-token: write</code> permission); for a private repository,
          create an upload token on the account page, store it as a repository secret and replace the
          <code>use-oidc</code> line with a <code>token</code> input.
        </p>
        <bs-tab-control [border]="true">
          @for (example of workflowExamples(); track example.key) {
            <bs-tab-page>
              <ng-container *bsTabPageHeader>{{ example.label }}</ng-container>
              <div class="p-3">
                @if (example.config; as config) {
                  <p class="small text-muted">{{ config.note }}</p>
                  <bs-code-snippet [code]="config.code" [language]="config.language" class="mb-3" />
                }
                <p class="small text-muted">{{ example.note }}</p>
                <bs-code-snippet [code]="example.code" language="yaml" />
              </div>
            </bs-tab-page>
          }
        </bs-tab-control>
      </bs-card-body>
    </bs-card>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class RepoSetupPanelComponent {
  /** The server's public base URL (Coverage:BaseUrl); falls back to location.origin. */
  baseUrl = input<string | undefined>();

  /** Example CI workflows per ecosystem, built against this deployment's URL. */
  readonly workflowExamples = computed<WorkflowExample[]>(() => {
    const url = this.baseUrl() || location.origin;
    const upload = (extra = '') => `      - name: Upload coverage
        uses: MintPlayer/CodeCoverage/action@master
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
          node-version: 22
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
          node-version: 22
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
          node-version: 22
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
          node-version: 22
          cache: npm
      - run: npm ci
      - run: npx nx run-many -t test --coverage
${upload(`
          files: 'coverage/**/lcov.info'`)}`),
      },
    ];
  });
}
