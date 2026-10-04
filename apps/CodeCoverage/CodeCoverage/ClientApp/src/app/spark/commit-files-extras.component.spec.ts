import { Component, input, provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { describe, expect, it, vi } from 'vitest';
import type { PersistentObject } from '@mintplayer/ng-spark/models';
import { SparkService } from '@mintplayer/ng-spark/services';
import { CommitFilesPanelComponent } from '../components/commit-files-panel/commit-files-panel.component';
import { settle } from '../../testing/test-utils';
import { CommitFilesExtrasComponent } from './commit-files-extras.component';

function po(attributes: Record<string, unknown>): PersistentObject {
  return {
    attributes: Object.entries(attributes).map(([name, value]) => ({ name, value })),
  } as unknown as PersistentObject;
}

@Component({ selector: 'app-commit-files-panel', template: '' })
class StubCommitFilesPanel {
  readonly provider = input<string>();
  readonly owner = input<string>();
  readonly name = input<string>();
  readonly sha = input<string>();
}

describe('CommitFilesExtrasComponent', () => {
  async function setup(commit: PersistentObject, get: (type: string, id: string) => Promise<unknown>) {
    const spark = { get: vi.fn(get) };
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), { provide: SparkService, useValue: spark }],
    });
    TestBed.overrideComponent(CommitFilesExtrasComponent, {
      remove: { imports: [CommitFilesPanelComponent] },
      add: { imports: [StubCommitFilesPanel] },
    });
    const fixture = TestBed.createComponent(CommitFilesExtrasComponent);
    fixture.componentRef.setInput('po', commit);
    await settle(fixture);
    return { fixture, spark };
  }

  function panel(fixture: ComponentFixture<unknown>): StubCommitFilesPanel | null {
    return fixture.debugElement.query(By.directive(StubCommitFilesPanel))?.componentInstance ?? null;
  }

  it('loads the referenced repository and hands the panel its forge, owner, name and sha', async () => {
    const { fixture, spark } = await setup(
      po({ Sha: 'abc123', Repository: 'Repositories/gitlab/7' }),
      () => Promise.resolve(po({ FullName: 'acme/widgets' })));

    expect(spark.get).toHaveBeenCalledWith('Repository', 'Repositories/gitlab/7');
    const target = panel(fixture)!;
    expect([target.provider(), target.owner(), target.name(), target.sha()]).toEqual(['gitlab', 'acme', 'widgets', 'abc123']);
  });

  it('renders no panel, and loads nothing, without a sha or a repository reference', async () => {
    for (const commit of [po({ Repository: 'repos/7' }), po({ Sha: 'abc', Repository: '' }), po({ Sha: 42, Repository: 'repos/7' })]) {
      TestBed.resetTestingModule();
      const { fixture, spark } = await setup(commit, () => Promise.resolve(po({})));
      expect(spark.get).not.toHaveBeenCalled();
      expect(panel(fixture)).toBeNull();
    }
  });

  // The forge comes from the repository's document id, so an id without one means no panel.
  it('renders no panel when the repository id carries no forge or the repository no owner/name', async () => {
    const cases: [string, PersistentObject][] = [
      ['repos/7', po({ FullName: 'acme/widgets' })],
      ['Repositories/github/7', po({ FullName: 'widgets' })],
      ['Repositories/github/7', po({})],
    ];
    for (const [repositoryId, repo] of cases) {
      TestBed.resetTestingModule();
      const { fixture } = await setup(po({ Sha: 'abc', Repository: repositoryId }), () => Promise.resolve(repo));
      expect(panel(fixture)).toBeNull();
    }
  });

  // Row security can refuse the repository; the commit page must still render, just without files.
  it('renders no panel when the repository cannot be loaded', async () => {
    const { fixture } = await setup(po({ Sha: 'abc', Repository: 'repos/7' }), () => Promise.reject(new Error('403')));

    expect(panel(fixture)).toBeNull();
    expect(fixture.componentInstance.target()).toBeNull();
  });
});
