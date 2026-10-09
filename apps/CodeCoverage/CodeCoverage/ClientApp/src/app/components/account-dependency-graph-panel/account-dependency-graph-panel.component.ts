import {
  afterNextRender, afterRenderEffect, ChangeDetectionStrategy, Component, computed, DestroyRef, effect, ElementRef, inject, input,
  signal, viewChild,
} from '@angular/core';
import { Router } from '@angular/router';
import { BsCardBodyComponent, BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import type cytoscape from 'cytoscape';
import { BrowseService, DependencyEcosystem, DependencyGraph, DependencyGraphPackage } from '../../services/browse.service';
import { ECOSYSTEM_COLORS, ECOSYSTEMS, edgeId, GraphElements, toElements } from './dependency-graph';

type Cytoscape = typeof cytoscape;

let cytoscapeLoader: Promise<Cytoscape> | null = null;

/**
 * Cytoscape and its dagre layout are loaded on first use, in their own lazy chunk, so only a page
 * that actually draws a graph pays for them. The extension is registered exactly once.
 */
function loadCytoscape(): Promise<Cytoscape> {
  cytoscapeLoader ??= Promise.all([import('cytoscape'), import('cytoscape-dagre')]).then(([core, dagre]) => {
    const factory = core.default;
    factory.use(dagre.default);
    return factory;
  });
  // A failed chunk load must not stick: the next attempt retries the import.
  cytoscapeLoader.catch(() => { cytoscapeLoader = null; });
  return cytoscapeLoader;
}

/** Reads a CSS custom property off <html>, where the light/dark theme sets them. */
function cssVar(name: string, fallback: string): string {
  const value = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  return value || fallback;
}

/** The graph's stylesheet, built from the current theme's colours. */
function buildStyle(): cytoscape.StylesheetJson {
  const body = cssVar('--bs-body-color', '#212529');
  const bodyBg = cssVar('--bs-body-bg', '#ffffff');
  const nodeBg = cssVar('--bs-tertiary-bg', '#f8f9fa');
  const border = cssVar('--bs-border-color', '#dee2e6');
  const primary = cssVar('--bs-primary', '#0d6efd');
  const warning = cssVar('--bs-warning', '#ffc107');
  const font = cssVar('--bs-body-font-family', 'system-ui, sans-serif');

  const sheet: { selector: string; style: Record<string, unknown> }[] = [
    {
      selector: 'node',
      style: {
        'shape': 'round-rectangle',
        'width': 'data(width)',
        'height': 30,
        'background-color': nodeBg,
        'border-width': 1.5,
        'border-color': border,
        'label': 'data(label)',
        'color': body,
        'font-family': font,
        'font-size': FONT_SIZE,
        'text-valign': 'center',
        'text-halign': 'center',
        'transition-property': 'opacity',
        'transition-duration': 150,
      },
    },
    { selector: 'node.private', style: { 'border-style': 'dashed' } },
    { selector: 'node.scan-error', style: { 'border-color': warning } },
    { selector: 'node.archived', style: { 'opacity': 0.5 } },
    { selector: 'node:active', style: { 'overlay-opacity': 0 } },
    {
      selector: 'edge',
      style: {
        'width': 2,
        'curve-style': 'bezier',
        'target-arrow-shape': 'triangle',
        'arrow-scale': 1.1,
        'label': 'data(label)',
        // The max width is set per edge by fitEdgeLabels() (90% of the edge's length); a longer label is cut with "…".
        'text-wrap': 'ellipsis',
        'color': body,
        'font-family': font,
        'font-size': FONT_SIZE,
        'text-background-color': bodyBg,
        'text-background-opacity': 0.85,
        'text-background-padding': 2,
        'text-rotation': 'autorotate',
        'transition-property': 'opacity',
        'transition-duration': 150,
      },
    },
    { selector: 'edge.dev', style: { 'line-style': 'dashed' } },
    // A label narrower than its ellipsis says nothing: hidden on edges too short to carry one.
    { selector: 'edge.no-label', style: { 'label': '' } },
    ...ECOSYSTEMS.map((eco) => {
      const color = cssVar(ECOSYSTEM_COLORS[eco].cssVar, ECOSYSTEM_COLORS[eco].fallback);
      return { selector: `edge[ecosystem = "${eco}"]`, style: { 'line-color': color, 'target-arrow-color': color } };
    }),
    { selector: 'edge:selected', style: { 'width': 4, 'font-weight': 'bold' } },
    { selector: 'node.highlighted', style: { 'border-color': primary, 'border-width': 2.5 } },
    { selector: 'edge.highlighted', style: { 'width': 3 } },
    { selector: '.faded', style: { 'opacity': 0.15 } },
    { selector: 'node.archived.faded', style: { 'opacity': 0.1 } },
  ];
  return sheet as unknown as cytoscape.StylesheetJson;
}

/** One label size for repositories and arrows alike. */
const FONT_SIZE = 12;

/** Below this many graph pixels an edge shows no label at all; "…" alone tells nothing. */
const MIN_LABEL_WIDTH = 24;

/**
 * Caps every edge's label at 90% of the edge's visible length (endpoint to endpoint, in graph
 * coordinates, so the ratio holds at every zoom level). Longer labels end in "…" (`text-wrap:
 * ellipsis`); the full package list stays one click away. Set as a style bypass, which survives the
 * stylesheet swap on a theme change.
 */
function fitEdgeLabels(edges: cytoscape.EdgeCollection): void {
  edges.forEach((edge) => {
    const from = edge.sourceEndpoint();
    const to = edge.targetEndpoint();
    const width = Math.floor(0.9 * Math.hypot(to.x - from.x, to.y - from.y));
    edge.toggleClass('no-label', width < MIN_LABEL_WIDTH);
    edge.style('text-max-width', `${Math.max(width, MIN_LABEL_WIDTH)}px`);
  });
}

interface SelectedEdge {
  from: string;
  to: string;
  packages: DependencyGraphPackage[];
}

/**
 * "Repository dependencies" card on the Account page: an interactive graph of which of the
 * account's repositories consume packages another one publishes (arrow = producer → consumer).
 * Pan, zoom and drag; hover a repository to highlight its neighbours; click one to open it; click
 * an arrow to list its packages. Hidden while the account has no edges, or when the endpoint
 * refuses (401/404), like the trend panel hides without history.
 */
@Component({
  selector: 'app-account-dependency-graph-panel',
  imports: [BsCardComponent, BsCardHeaderComponent, BsCardBodyComponent],
  template: `
    @if (state() === 'pending' || state() === 'empty') {
      <bs-card class="mt-3 d-block">
        <bs-card-header><i class="bi bi-diagram-3"></i> Repository dependencies</bs-card-header>
        <bs-card-body>
          <div class="small text-muted">
            @if (state() === 'pending') {
              The repositories of this account have not been scanned yet. The graph appears after the first scan, which
              runs nightly, after a push that changes a manifest, or when a repository is added.
            } @else {
              None of this account's repositories uses a package that another of them publishes.
            }
          </div>
        </bs-card-body>
      </bs-card>
    }
    @if (state() === 'graph') {
      <bs-card class="mt-3 d-block">
        <bs-card-header><i class="bi bi-diagram-3"></i> Repository dependencies</bs-card-header>
        <bs-card-body>
          <div class="d-flex flex-wrap align-items-center gap-2 mb-2">
            <button type="button" class="btn btn-sm btn-outline-secondary" (click)="fit()">
              <i class="bi bi-arrows-fullscreen"></i> Fit
            </button>
            <button type="button" class="btn btn-sm"
                    [class.btn-secondary]="showIsolated()" [class.btn-outline-secondary]="!showIsolated()"
                    [attr.aria-pressed]="showIsolated()" (click)="showIsolated.set(!showIsolated())">
              <i class="bi" [class.bi-eye]="showIsolated()" [class.bi-eye-slash]="!showIsolated()"></i>
              Show repositories without dependencies
            </button>
            <span class="d-flex flex-wrap gap-2 small ms-auto">
              @for (eco of legend(); track eco.name) {
                <span class="d-inline-flex align-items-center gap-1">
                  <span class="swatch" [style.background]="eco.color"></span>{{ eco.name }}
                </span>
              }
            </span>
          </div>

          <div #canvas class="graph" role="img"
               aria-label="Dependency graph of this account's repositories; arrows point from the repository publishing a package to the one using it"></div>

          <div class="small text-muted mt-1">
            Arrows point from the repository that publishes a package to the one that uses it; dashed arrows carry dev-only
            dependencies. Click a repository to open it, click an arrow to list its packages.
          </div>
          @if (loadFailed()) {
            <div class="small text-danger mt-1">The graph could not be loaded.</div>
          }

          @if (selected(); as edge) {
            <div class="mt-3">
              <div class="fw-semibold mb-1">{{ edge.from }} <i class="bi bi-arrow-right"></i> {{ edge.to }}</div>
              <ul class="list-unstyled mb-0">
                @for (pkg of edge.packages; track $index) {
                  <li class="d-flex flex-wrap align-items-baseline gap-2 py-1 border-bottom">
                    <span class="badge" [style.background-color]="colorOf(pkg.ecosystem)">{{ pkg.ecosystem }}</span>
                    <code>{{ pkg.name }}</code>
                    @if (pkg.dev) { <span class="badge text-bg-secondary">dev</span> }
                    <span class="small text-muted text-break">{{ pkg.manifestPath }}</span>
                  </li>
                }
              </ul>
            </div>
          }
        </bs-card-body>
      </bs-card>
    }
  `,
  styles: `
    .graph {
      height: 420px;
      width: 100%;
      border: 1px solid var(--bs-border-color);
      border-radius: var(--bs-border-radius);
      background: var(--bs-body-bg);
    }
    .swatch { display: inline-block; width: 1.25rem; height: 3px; border-radius: 2px; }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class AccountDependencyGraphPanelComponent {
  private readonly browse = inject(BrowseService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  /** Canonical forge spelling, e.g. "github". Sourced from the PO's document id (`forgeOf`). */
  provider = input.required<string>();
  login = input.required<string>();

  private readonly graph = signal<DependencyGraph | null>(null);
  readonly showIsolated = signal(false);
  readonly selected = signal<SelectedEdge | null>(null);
  readonly loadFailed = signal(false);

  private readonly canvas = viewChild<ElementRef<HTMLElement>>('canvas');

  readonly elements = computed<GraphElements | null>(() => {
    const graph = this.graph();
    return graph ? toElements(graph, this.showIsolated()) : null;
  });

  readonly hasEdges = computed(() => (this.elements()?.edges.length ?? 0) > 0);

  /**
   * hidden: no access, unknown account or an account without repositories; pending: no repository
   * scanned yet; empty: scanned, but no repository uses a package another one publishes.
   */
  readonly state = computed<'hidden' | 'pending' | 'empty' | 'graph'>(() => {
    const graph = this.graph();
    if (!graph || graph.nodes.length === 0) return 'hidden';
    if (this.hasEdges()) return 'graph';
    return graph.nodes.some((n) => n.scannedAt !== null) ? 'empty' : 'pending';
  });

  readonly legend = computed(() => (this.elements()?.ecosystems ?? []).map((name) => ({ name, color: this.colorOf(name) })));

  private cy: cytoscape.Core | null = null;
  private drawToken = 0;
  private destroyed = false;

  constructor() {
    effect(async () => {
      const provider = this.provider();
      const login = this.login();
      this.selected.set(null);
      try {
        this.graph.set(await this.browse.getDependencyGraph(provider, login));
      } catch {
        // 401/404 (no access, unknown account) and transport errors alike: the card stays hidden.
        this.graph.set(null);
      }
    });

    afterRenderEffect(() => {
      const host = this.canvas()?.nativeElement;
      const elements = this.elements();
      if (!host || !elements) {
        this.teardown();
        return;
      }
      void this.draw(host, elements);
    });

    // Follow the light/dark theme: ng-bootstrap flips data-bs-theme on <html>, and "auto" follows the OS.
    afterNextRender(() => {
      const restyle = () => this.cy?.style(buildStyle());
      const observer = new MutationObserver(restyle);
      observer.observe(document.documentElement, { attributes: true, attributeFilter: ['data-bs-theme', 'class'] });
      const media = window.matchMedia?.('(prefers-color-scheme: dark)');
      media?.addEventListener?.('change', restyle);
      this.destroyRef.onDestroy(() => {
        observer.disconnect();
        media?.removeEventListener?.('change', restyle);
      });
    });

    this.destroyRef.onDestroy(() => {
      this.destroyed = true;
      this.teardown();
    });
  }

  colorOf(ecosystem: DependencyEcosystem): string {
    const { cssVar: name, fallback } = ECOSYSTEM_COLORS[ecosystem];
    return `var(${name}, ${fallback})`;
  }

  fit(): void {
    const cy = this.cy;
    if (!cy) return;
    cy.animate({ fit: { eles: cy.elements(), padding: 24 } }, { duration: 200 });
  }

  private teardown(): void {
    this.drawToken++;
    this.cy?.destroy();
    this.cy = null;
  }

  private async draw(host: HTMLElement, elements: GraphElements): Promise<void> {
    const token = ++this.drawToken;
    let factory: Cytoscape;
    try {
      factory = await loadCytoscape();
    } catch {
      this.loadFailed.set(true);
      return;
    }
    if (token !== this.drawToken || this.destroyed) return;
    this.loadFailed.set(false);

    this.cy?.destroy();
    this.selected.set(null);

    const cy = factory({
      container: host,
      elements: [...elements.nodes, ...elements.edges] as cytoscape.ElementDefinition[],
      style: buildStyle(),
      layout: {
        name: 'dagre', rankDir: 'LR', nodeSep: 24, rankSep: 90, edgeSep: 10, padding: 24, fit: true,
      } as unknown as cytoscape.LayoutOptions,
      minZoom: 0.2,
      maxZoom: 2.5,
      boxSelectionEnabled: false,
      autounselectify: false,
    });
    // Nodes are draggable but never "selected": selection is reserved for the edge whose packages are listed.
    cy.nodes().unselectify();

    // dagre runs synchronously inside the constructor, so positions are final here; dragging a node
    // changes the length of its edges, so their labels are refitted while it moves.
    fitEdgeLabels(cy.edges());
    cy.on('position', 'node', (e) => fitEdgeLabels(e.target.connectedEdges()));

    const focus = (hood: cytoscape.CollectionReturnValue) => cy.batch(() => {
      cy.elements().not(hood).addClass('faded');
      hood.addClass('highlighted');
    });
    const blur = () => cy.batch(() => cy.elements().removeClass('faded highlighted'));

    // A canvas has no per-element title, so the container's title follows the pointer: the browser's
    // native tooltip then shows the full text an edge label may have cut short.
    cy.on('mouseover', 'node', (e) => {
      host.style.cursor = 'pointer';
      host.title = this.tooltipForNode(e.target.id());
      focus(e.target.closedNeighborhood());
    });
    cy.on('mouseover', 'edge', (e) => {
      host.style.cursor = 'pointer';
      host.title = this.tooltipForEdge(e.target.id());
      focus(e.target.union(e.target.connectedNodes()));
    });
    cy.on('mouseout', 'node, edge', () => { host.style.cursor = ''; host.removeAttribute('title'); blur(); });
    cy.on('grab', 'node', blur);

    cy.on('tap', 'node', (e) => {
      void this.router.navigate(['/po', 'repository', e.target.id()]);
    });
    cy.on('tap', 'edge', (e) => this.select(e.target.id()));
    cy.on('tap', (e) => {
      if (e.target === cy) this.selected.set(null);
    });

    this.cy = cy;
  }

  private tooltipForNode(id: string): string {
    return this.graph()?.nodes.find((n) => n.id === id)?.fullName ?? '';
  }

  private tooltipForEdge(id: string): string {
    const graph = this.graph();
    const edge = graph?.edges.find((e) => edgeId(e) === id);
    if (!graph || !edge) return '';
    const nameOf = (nodeId: string) => graph.nodes.find((n) => n.id === nodeId)?.name ?? nodeId;
    const packages = [...new Set(edge.dependencies.map((d) => `${d.ecosystem}: ${d.name}${d.dev ? ' (dev)' : ''}`))].sort();
    return `${nameOf(edge.from)} → ${nameOf(edge.to)}\n${packages.join('\n')}`;
  }

  private select(id: string): void {
    const graph = this.graph();
    const edge = graph?.edges.find((e) => edgeId(e) === id);
    if (!graph || !edge) {
      this.selected.set(null);
      return;
    }
    const nameOf = (nodeId: string) => graph.nodes.find((n) => n.id === nodeId)?.name ?? nodeId;
    const packages = [...edge.dependencies].sort((a, b) =>
      ECOSYSTEMS.indexOf(a.ecosystem) - ECOSYSTEMS.indexOf(b.ecosystem)
      || a.name.localeCompare(b.name)
      || a.manifestPath.localeCompare(b.manifestPath));
    this.selected.set({ from: nameOf(edge.from), to: nameOf(edge.to), packages });
  }
}
