import type { DependencyEcosystem, DependencyGraph, DependencyGraphEdge, DependencyGraphPackage } from '../../services/browse.service';

/**
 * Pure mapping from the `/dependency-graph` response to Cytoscape element definitions. Kept free of
 * Cytoscape itself (the library is lazy-loaded by the panel) so it can be unit-tested directly.
 */

/** Fixed order: decides ties for the dominant ecosystem and the legend order. */
export const ECOSYSTEMS: readonly DependencyEcosystem[] = ['npm', 'nuget', 'pip', 'composer', 'actions', 'docker'];

/**
 * Each ecosystem's colour as a Bootstrap custom property, with a fallback for when the property is
 * absent. Read at style time, so the graph follows the light/dark theme.
 */
export const ECOSYSTEM_COLORS: Readonly<Record<DependencyEcosystem, { cssVar: string; fallback: string }>> = {
  npm: { cssVar: '--bs-red', fallback: '#dc3545' },
  nuget: { cssVar: '--bs-purple', fallback: '#6f42c1' },
  pip: { cssVar: '--bs-green', fallback: '#198754' },
  composer: { cssVar: '--bs-orange', fallback: '#fd7e14' },
  actions: { cssVar: '--bs-gray-600', fallback: '#6c757d' },
  docker: { cssVar: '--bs-cyan', fallback: '#0dcaf0' },
};

export interface GraphNodeData {
  id: string;
  label: string;
  name: string;
  fullName: string;
  isPrivate: boolean;
  archived: boolean;
  scanError: string | null;
  /** Approximate box width for the label, since Cytoscape no longer sizes nodes to their label. */
  width: number;
}

export interface GraphEdgeData {
  id: string;
  source: string;
  target: string;
  label: string;
  ecosystem: DependencyEcosystem;
  count: number;
}

export interface GraphElement<T> {
  group: 'nodes' | 'edges';
  data: T;
  classes: string;
}

export interface GraphElements {
  nodes: GraphElement<GraphNodeData>[];
  edges: GraphElement<GraphEdgeData>[];
  /** Ecosystems present on the drawn edges, in `ECOSYSTEMS` order, for the legend. */
  ecosystems: DependencyEcosystem[];
}

/** One package per (ecosystem, name): the same package declared in two manifests counts once. */
export function distinctPackages(dependencies: readonly DependencyGraphPackage[]): DependencyGraphPackage[] {
  const seen = new Set<string>();
  return dependencies.filter((d) => {
    const key = `${d.ecosystem}\u0000${d.name}`;
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}

/** The ecosystem most of the edge's packages belong to; ties go to the earlier one in `ECOSYSTEMS`. */
export function dominantEcosystem(dependencies: readonly DependencyGraphPackage[]): DependencyEcosystem {
  const counts = new Map<DependencyEcosystem, number>();
  for (const d of distinctPackages(dependencies)) counts.set(d.ecosystem, (counts.get(d.ecosystem) ?? 0) + 1);
  let best: DependencyEcosystem = 'npm';
  let bestCount = -1;
  for (const eco of ECOSYSTEMS) {
    const c = counts.get(eco) ?? 0;
    if (c > bestCount) { best = eco; bestCount = c; }
  }
  return best;
}

/** The single package's name, or "N packages". */
export function edgeLabel(dependencies: readonly DependencyGraphPackage[]): string {
  const packages = distinctPackages(dependencies);
  return packages.length === 1 ? packages[0].name : `${packages.length} packages`;
}

export function edgeId(edge: Pick<DependencyGraphEdge, 'from' | 'to'>): string {
  return `${edge.from}->${edge.to}`;
}

/**
 * Nodes and edges to draw. Edges whose endpoints are not among the nodes are dropped, as are edges
 * without packages. Unless `showIsolated`, only repositories on at least one drawn edge are kept.
 */
export function toElements(graph: DependencyGraph, showIsolated: boolean): GraphElements {
  const nodeIds = new Set(graph.nodes.map((n) => n.id));
  const edges = graph.edges.filter((e) => nodeIds.has(e.from) && nodeIds.has(e.to) && e.dependencies.length > 0);

  const connected = new Set<string>();
  for (const e of edges) { connected.add(e.from); connected.add(e.to); }

  const nodes = graph.nodes
    .filter((n) => showIsolated || connected.has(n.id))
    .map<GraphElement<GraphNodeData>>((n) => {
      const label = n.isPrivate ? `\u{1F512} ${n.name}` : n.name;
      const classes = [
        n.isPrivate ? 'private' : '',
        n.archived ? 'archived' : '',
        n.scanError ? 'scan-error' : '',
        connected.has(n.id) ? '' : 'isolated',
      ].filter(Boolean).join(' ');
      return {
        group: 'nodes',
        data: {
          id: n.id, label, name: n.name, fullName: n.fullName,
          isPrivate: n.isPrivate, archived: n.archived, scanError: n.scanError,
          width: Math.max(60, Math.round(label.length * 7.2) + 24),
        },
        classes,
      };
    });

  const present = new Set<DependencyEcosystem>();
  const edgeElements = edges.map<GraphElement<GraphEdgeData>>((e) => {
    const ecosystem = dominantEcosystem(e.dependencies);
    present.add(ecosystem);
    const dev = e.dependencies.every((d) => d.dev);
    return {
      group: 'edges',
      data: {
        id: edgeId(e), source: e.from, target: e.to,
        label: edgeLabel(e.dependencies), ecosystem,
        count: distinctPackages(e.dependencies).length,
      },
      classes: dev ? 'dev' : '',
    };
  });

  return { nodes, edges: edgeElements, ecosystems: ECOSYSTEMS.filter((eco) => present.has(eco)) };
}
