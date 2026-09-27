import { Component, Directive, inject, input, provideZonelessChangeDetection, TemplateRef, ViewContainerRef } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { BsCodeSnippetComponent } from '@mintplayer/ng-bootstrap/code-snippet';
import { BsTabControlComponent, BsTabPageComponent, BsTabPageHeaderDirective } from '@mintplayer/ng-bootstrap/tab-control';
import { beforeEach, describe, expect, it } from 'vitest';
import { settle } from '../../../testing/test-utils';
import { RepoSetupPanelComponent } from './repo-setup-panel.component';

/*
 * The tab control and the code snippet are Lit elements jsdom cannot host. These stand-ins render
 * every page and header inline, so a spec sees all examples at once.
 */
@Component({ selector: 'bs-tab-control', template: '<ng-content />' })
class StubTabControl {
  readonly border = input<boolean>(false);
}

@Component({ selector: 'bs-tab-page', template: '<ng-content />' })
class StubTabPage {}

@Directive({ selector: '[bsTabPageHeader]' })
class StubTabPageHeader {
  constructor() {
    inject(ViewContainerRef).createEmbeddedView(inject(TemplateRef));
  }
}

@Component({ selector: 'bs-code-snippet', template: '' })
class StubCodeSnippet {
  readonly code = input<string>('');
  readonly language = input<string>('');
}

describe('RepoSetupPanelComponent', () => {
  let fixture: ComponentFixture<RepoSetupPanelComponent>;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [RepoSetupPanelComponent],
      providers: [provideZonelessChangeDetection()],
    });
    TestBed.overrideComponent(RepoSetupPanelComponent, {
      remove: { imports: [BsTabControlComponent, BsTabPageComponent, BsTabPageHeaderDirective, BsCodeSnippetComponent] },
      add: { imports: [StubTabControl, StubTabPage, StubTabPageHeader, StubCodeSnippet] },
    });
    fixture = TestBed.createComponent(RepoSetupPanelComponent);
  });

  async function render(baseUrl?: string) {
    if (baseUrl !== undefined) fixture.componentRef.setInput('baseUrl', baseUrl);
    await settle(fixture);
  }

  const snippets = () => fixture.debugElement.queryAll(By.directive(StubCodeSnippet)).map((d) => d.componentInstance as StubCodeSnippet);
  const text = () => (fixture.nativeElement as HTMLElement).textContent ?? '';

  // One forge configured: the per-language tabs render directly, with the intro above them
  // rather than inside a one-item forge tab strip.
  it('renders the single GitHub guide without a forge tab', async () => {
    await render('https://cov.example.test');

    expect(fixture.componentInstance.singleGuide()).toBe(true);
    expect(text()).not.toContain('GitHub Actions');
    const intro = fixture.nativeElement.querySelector('p.text-muted.small') as HTMLElement;
    expect(intro.innerHTML).toContain('<code>.github/workflows/ci.yml</code>');
    for (const label of ['.NET', 'Node.js', 'Angular', 'React', 'Python', 'Java', 'Nx']) {
      expect(text()).toContain(label);
    }
  });

  it('builds every workflow against the configured base URL and the pinned action tag', async () => {
    await render('https://cov.example.test');

    const workflows = snippets().filter((s) => s.language() === 'yaml');
    expect(workflows).toHaveLength(7);
    for (const workflow of workflows) {
      expect(workflow.code()).toContain('url: https://cov.example.test');
      expect(workflow.code()).toContain('uses: MintPlayer/MintPlayer.Spark/apps/CodeCoverage/action@coverage-upload-v1');
      expect(workflow.code()).toContain('id-token: write');
      expect(workflow.code()).toContain('finish: true');
    }
    expect(workflows.find((w) => w.code().includes('mvn -B verify'))!.code()).toContain("files: '**/jacoco.xml'");
    expect(workflows.find((w) => w.code().includes('nx run-many'))!.code()).toContain("files: 'coverage/**/lcov.info'");
  });

  it('shows the Nx per-project config above its workflow', async () => {
    await render('https://cov.example.test');

    const config = snippets().find((s) => s.language() === 'ts')!;
    expect(config.code()).toContain("reportsDirectory: '../../coverage/libs/my-lib'");
    expect(text()).toContain('declare that folder as the target');
  });

  it('falls back to location.origin without a configured base URL', async () => {
    await render();
    expect(snippets()[0].code()).toContain(`url: ${location.origin}`);

    fixture.componentRef.setInput('baseUrl', '');
    await settle(fixture);
    expect(snippets()[0].code()).toContain(`url: ${location.origin}`);
  });
});
