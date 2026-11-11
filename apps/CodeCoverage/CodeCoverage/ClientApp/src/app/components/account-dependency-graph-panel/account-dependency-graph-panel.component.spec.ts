import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { afterAll, afterEach, beforeAll, describe, expect, it } from 'vitest';
import type { DependencyGraph, DependencyGraphNode, DependencyGraphPackage } from '../../services/browse.service';
import { BrowseServiceStub, createBrowseStub, provideBrowseStub, settle } from '../../../testing/test-utils';
import { AccountDependencyGraphPanelComponent } from './account-dependency-graph-panel.component';

function node(id: string, name: string, extra: Partial<DependencyGraphNode> = {}): DependencyGraphNode {
  return { id, name, fullName: `acme/${name}`, isPrivate: false, archived: false, scannedAt: '2026-11-01T00:00:00Z', scanError: null, ...extra };
}

function pkg(ecosystem: DependencyGraphPackage['ecosystem'], name: string, extra: Partial<DependencyGraphPackage> = {}): DependencyGraphPackage {
  return { ecosystem, name, manifestPath: 'package.json', dev: false, ...extra };
}

const graph: DependencyGraph = {
  nodes: [node('Repositories/github/1', 'lib'), node('Repositories/github/2', 'app'), node('Repositories/github/3', 'lonely'), node('Repositories/github/4', 'web')],
  edges: [
    { from: 'Repositories/github/1', to: 'Repositories/github/2', dependencies: [pkg('nuget', 'Acme.Lib', { manifestPath: 'App.csproj' })] },
    { from: 'Repositories/github/1', to: 'Repositories/github/4', dependencies: [pkg('npm', '@acme/lib', { dev: true })] },
  ],
};

/**
 * jsdom has no 2d canvas, and cytoscape's renderer needs one. A context whose every member is a no-op (and whose
 * measureText measures nothing) lets the graph be constructed; nothing here asserts pixels.
 */
function fakeContext(): CanvasRenderingContext2D {
  const handler: ProxyHandler<object> = {
    get: (_target, key) => (key === 'measureText' ? () => ({ width: 0 }) : key === 'canvas' ? document.createElement('canvas') : () => proxy),
    set: () => true,
  };
  const proxy: object = new Proxy({}, handler);
  return proxy as CanvasRenderingContext2D;
}

/**
 * The "Repository dependencies" card: hidden without access or repositories, the pending and empty notes, and with
 * edges the toolbar, the legend's ecosystem toggles and the isolated-repositories switch. The canvas itself is
 * cytoscape's; these cases only assert what the card does around it.
 */
describe('AccountDependencyGraphPanelComponent', () => {
  let fixture: ComponentFixture<AccountDependencyGraphPanelComponent>;
  let browse: BrowseServiceStub;

  async function create(getDependencyGraph: () => Promise<DependencyGraph>) {
    browse = createBrowseStub({ getDependencyGraph });
    TestBed.configureTestingModule({
      imports: [AccountDependencyGraphPanelComponent],
      providers: [provideZonelessChangeDetection(), provideNoopAnimations(), provideRouter([]), provideBrowseStub(browse)],
    });
    fixture = TestBed.createComponent(AccountDependencyGraphPanelComponent);
    fixture.componentRef.setInput('provider', 'github');
    fixture.componentRef.setInput('login', 'acme');
    await settle(fixture);
  }

  const getContext = HTMLCanvasElement.prototype.getContext;
  beforeAll(() => {
    HTMLCanvasElement.prototype.getContext = (() => fakeContext()) as unknown as typeof getContext;
  });
  afterAll(() => {
    HTMLCanvasElement.prototype.getContext = getContext;
  });

  afterEach(() => fixture?.destroy());

  const text = () => (fixture.nativeElement as HTMLElement).textContent ?? '';
  const component = () => fixture.componentInstance;

  it('is hidden when the server refuses or the account has no repositories', async () => {
    await create(() => Promise.reject(new Error('404')));
    expect(component().state()).toBe('hidden');
    expect(text()).not.toContain('Repository dependencies');

    TestBed.resetTestingModule();
    await create(() => Promise.resolve({ nodes: [], edges: [] }));
    expect(component().state()).toBe('hidden');
  });

  it('says the repositories are not scanned yet, or that none uses another\'s package', async () => {
    await create(() => Promise.resolve({ nodes: [node('Repositories/github/1', 'lib', { scannedAt: null })], edges: [] }));
    expect(component().state()).toBe('pending');
    expect(text()).toContain('have not been scanned yet');

    TestBed.resetTestingModule();
    await create(() => Promise.resolve({ nodes: [node('Repositories/github/1', 'lib')], edges: [] }));
    expect(component().state()).toBe('empty');
    expect(text()).toContain('None of this account\'s repositories uses a package');
  });

  it('with edges shows the graph card, a legend per ecosystem, and asks the server for this account', async () => {
    await create(() => Promise.resolve(graph));

    expect(browse.getDependencyGraph).toHaveBeenCalledWith('github', 'acme');
    expect(component().state()).toBe('graph');
    expect(component().legend().map((l) => [l.name, l.visible])).toEqual(expect.arrayContaining([['nuget', true], ['npm', true]]));
    expect(component().elements()!.nodes.map((n) => n.data.name)).not.toContain('lonely');
    expect(text()).toContain('Show repositories without dependencies');
    expect(component().colorOf('npm')).toMatch(/^var\(--/);
  });

  it('unticking an ecosystem hides it in the legend state, ticking it shows it again', async () => {
    await create(() => Promise.resolve(graph));

    component().setEcosystemVisible('npm', false);
    expect(component().hiddenEcosystems().has('npm')).toBe(true);
    expect(component().legend().find((l) => l.name === 'npm')!.visible).toBe(false);

    component().setEcosystemVisible('npm', true);
    expect(component().hiddenEcosystems().has('npm')).toBe(false);
  });

  it('tapping an arrow lists its packages, tapping the background clears them, and hiding its ecosystem unselects it', async () => {
    await create(() => Promise.resolve(graph));
    type Edge = { data(key: string): string; select(): void; emit(event: string): void };
    type Cy = { edges(): { filter(keep: (e: Edge) => boolean): Edge[] }; emit(event: string): void };
    const cyOf = () => (component() as unknown as { cy: Cy | null }).cy;
    for (let i = 0; i < 20 && !cyOf(); i++) await settle(fixture);
    const cy = cyOf()!;
    expect(cy).not.toBeNull();

    const nuget = cy.edges().filter((e) => e.data('ecosystem') === 'nuget')[0];
    nuget.emit('tap');
    expect(component().selected()).toEqual({ from: 'lib', to: 'app', packages: [graph.edges[0].dependencies[0]] });

    cy.emit('tap');
    expect(component().selected()).toBeNull();

    nuget.emit('tap');
    nuget.select();
    component().setEcosystemVisible('nuget', false);
    await settle(fixture);
    expect(component().selected()).toBeNull();
  });

  it('the isolated-repositories switch adds the repositories without an edge', async () => {
    await create(() => Promise.resolve(graph));
    const before = component().elements()!.nodes.length;

    const toggle = [...(fixture.nativeElement as HTMLElement).querySelectorAll('button')].find((b) => b.textContent?.includes('Show repositories without'))!;
    toggle.click();
    await settle(fixture);

    expect(component().showIsolated()).toBe(true);
    expect(toggle.getAttribute('aria-pressed')).toBe('true');
    expect(component().elements()!.nodes.length).toBe(before + 1);
  });
});
