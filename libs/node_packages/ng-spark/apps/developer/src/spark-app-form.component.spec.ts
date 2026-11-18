import { TestBed } from '@angular/core/testing';
// eslint-disable-next-line @typescript-eslint/no-deprecated -- ng-bootstrap components emit synthetic animation props
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { describe, expect, it } from 'vitest';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import type { SparkAppFields, SparkWebhookCatalog } from '@mintplayer/ng-spark/apps/core';
import { SparkAppFormComponent, accessOf, permissionResources, withAccess } from './spark-app-form.component';

const catalog: SparkWebhookCatalog = {
  events: [
    { name: 'report.completed', summary: 'A report completed.', permission: 'reports:read' },
    { name: 'note.created', summary: 'A note was created.', permission: 'notes:read' },
    { name: 'site.announced', summary: 'Anyone may receive it.', permission: null },
  ],
  permissions: ['notes:read', 'reports:read', 'reports:write'],
};

function render(value: SparkAppFields | null, isNew: boolean) {
  TestBed.configureTestingModule({
    providers: [
      // eslint-disable-next-line @typescript-eslint/no-deprecated
      provideNoopAnimations(),
      { provide: SparkLanguageService, useValue: { t: (key: string) => key } },
    ],
  });
  const fixture = TestBed.createComponent(SparkAppFormComponent);
  fixture.componentRef.setInput('catalog', catalog);
  fixture.componentRef.setInput('value', value);
  fixture.componentRef.setInput('isNew', isNew);
  fixture.detectChanges();
  const emitted: SparkAppFields[] = [];
  fixture.componentInstance.saved.subscribe(fields => emitted.push(fields));
  return { component: fixture.componentInstance, emitted };
}

const existing: SparkAppFields = {
  slug: 'ci-bot', name: 'CI bot', description: 'Builds things', homepageUrl: 'https://ci.example', webhookUrl: 'https://ci.example/hook',
  setupUrl: null, public: true, permissions: ['reports:write', 'notes:read'], events: ['report.completed', 'note.created'],
};

describe('SparkAppFormComponent', () => {
  it('offers each resource once, at none, read or write; write is read as well', () => {
    expect(permissionResources(['reports:read', 'reports:write', 'notes:read', 'my.ns-x:write'])).toEqual(['my.ns-x', 'notes', 'reports']);
    expect(accessOf(['reports:write'], 'reports')).toBe('write');
    expect(accessOf(['reports:read'], 'reports')).toBe('read');
    expect(accessOf(['notes:read'], 'reports')).toBe('none');
    expect(withAccess(['reports:read', 'notes:read'], 'reports', 'write')).toEqual(['notes:read', 'reports:write']);
    expect(withAccess(['reports:write', 'notes:read'], 'reports', 'none')).toEqual(['notes:read']);
  });

  it('narrowing a permission drops the events it no longer covers', () => {
    const { component } = render(existing, false);
    component.setAccess('reports', 'read');
    expect(component.fields().events).toEqual(['report.completed', 'note.created']);
    component.setAccess('reports', 'none');
    expect(component.fields().permissions).toEqual(['notes:read']);
    expect(component.fields().events).toEqual(['note.created']);
  });

  it('a change clears an emptied field with "" (null would leave it), a new App sends null', () => {
    const edit = render(existing, false);
    edit.component.patch({ webhookUrl: '  ', description: '' });
    edit.component.submit();
    expect(edit.emitted[0]).toMatchObject({ webhookUrl: '', description: '', setupUrl: '', homepageUrl: 'https://ci.example' });

    TestBed.resetTestingModule();
    const created = render(null, true);
    created.component.patch({ slug: ' new-app ', name: ' New ', webhookUrl: '' });
    created.component.submit();
    expect(created.emitted[0]).toMatchObject({ slug: 'new-app', name: 'New', webhookUrl: null, description: null, events: [], permissions: [] });
  });
});
