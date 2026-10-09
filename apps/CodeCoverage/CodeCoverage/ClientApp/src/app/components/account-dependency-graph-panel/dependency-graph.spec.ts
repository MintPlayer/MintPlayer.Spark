import { describe, expect, it } from 'vitest';
import type { DependencyGraph, DependencyGraphNode, DependencyGraphPackage } from '../../services/browse.service';
import { dominantEcosystem, edgeLabel, toElements } from './dependency-graph';

function node(id: string, name: string, extra: Partial<DependencyGraphNode> = {}): DependencyGraphNode {
  return { id, name, fullName: `acme/${name}`, isPrivate: false, archived: false, scannedAt: null, scanError: null, ...extra };
}

function pkg(ecosystem: DependencyGraphPackage['ecosystem'], name: string, extra: Partial<DependencyGraphPackage> = {}): DependencyGraphPackage {
  return { ecosystem, name, manifestPath: 'package.json', dev: false, ...extra };
}

const graph: DependencyGraph = {
  nodes: [
    node('Repositories/github/1', 'lib'),
    node('Repositories/github/2', 'app', { isPrivate: true }),
    node('Repositories/github/3', 'old', { archived: true, scanError: 'boom' }),
    node('Repositories/github/4', 'lonely'),
  ],
  edges: [
    { from: 'Repositories/github/1', to: 'Repositories/github/2', dependencies: [pkg('npm', '@acme/lib')] },
    {
      from: 'Repositories/github/1', to: 'Repositories/github/3',
      dependencies: [pkg('nuget', 'Acme.Lib', { dev: true }), pkg('nuget', 'Acme.Lib', { manifestPath: 'b.csproj', dev: true }), pkg('npm', 'x', { dev: true }), pkg('nuget', 'Acme.Two', { dev: true })],
    },
    // Dangling endpoint and empty edge are dropped.
    { from: 'Repositories/github/1', to: 'Repositories/github/99', dependencies: [pkg('npm', 'y')] },
    { from: 'Repositories/github/2', to: 'Repositories/github/4', dependencies: [] },
  ],
};

describe('toElements', () => {
  it('keeps only connected repositories by default, with edges producer -> consumer', () => {
    const { nodes, edges, ecosystems } = toElements(graph, false);

    expect(nodes.map((n) => n.data.name)).toEqual(['lib', 'app', 'old']);
    expect(edges.map((e) => [e.data.source, e.data.target])).toEqual([
      ['Repositories/github/1', 'Repositories/github/2'],
      ['Repositories/github/1', 'Repositories/github/3'],
    ]);
    expect(ecosystems).toEqual(['npm', 'nuget']);
  });

  it('includes repositories without dependencies when asked, marked isolated', () => {
    const lonely = toElements(graph, true).nodes.find((n) => n.data.name === 'lonely');
    expect(lonely?.classes).toBe('isolated');
  });

  it('marks private, archived and failed-scan repositories', () => {
    const [lib, app, old] = toElements(graph, false).nodes;
    expect(lib.classes).toBe('');
    expect(app.classes).toBe('private');
    expect(app.data.label).toContain('app');
    expect(app.data.label).not.toBe('app');
    expect(old.classes).toBe('archived scan-error');
  });

  it('labels an edge with its single package, or the distinct package count, coloured by the dominant ecosystem', () => {
    const [single, multi] = toElements(graph, false).edges;
    expect(single.data).toMatchObject({ label: '@acme/lib', ecosystem: 'npm', count: 1 });
    expect(single.classes).toBe('');
    expect(multi.data).toMatchObject({ label: '3 packages', ecosystem: 'nuget', count: 3 });
    expect(multi.classes).toBe('dev');
  });
});

describe('edge helpers', () => {
  it('breaks ecosystem ties by the fixed ecosystem order', () => {
    expect(dominantEcosystem([pkg('docker', 'a'), pkg('pip', 'b')])).toBe('pip');
  });

  it('counts a package declared in two manifests once', () => {
    expect(edgeLabel([pkg('npm', 'a'), pkg('npm', 'a', { manifestPath: 'web/package.json' })])).toBe('a');
  });
});
