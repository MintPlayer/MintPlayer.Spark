import { ChangeDetectionStrategy, Component, computed, DestroyRef, effect, HostListener, inject, signal, untracked } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsButtonTypeDirective } from '@mintplayer/ng-bootstrap/button-type';
import { BsCheckboxComponent } from '@mintplayer/ng-bootstrap/checkbox';
import { BsFormComponent, BsFormControlDirective } from '@mintplayer/ng-bootstrap/form';
import { BsModalComponent, BsModalFooterDirective, BsModalHeaderDirective } from '@mintplayer/ng-bootstrap/modal';
import { BsOffcanvasComponent, BsOffcanvasHeaderDirective } from '@mintplayer/ng-bootstrap/offcanvas';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { BsTabControlComponent, BsTabPageComponent, BsTabPageHeaderDirective } from '@mintplayer/ng-bootstrap/tab-control';
import { SparkAuthService } from '@mintplayer/ng-spark/auth/core';
import { SparkIconComponent } from '@mintplayer/ng-spark/icon';
import { apiError, IconicApi, type SnapshotInfo } from '../../api/iconic-api';
import { EditorCanvasComponent } from '../../editor/canvas/editor-canvas.component';
import { normalizeScene } from '../../editor/core/model';
import { EditorStore } from '../../editor/editor-store';
import { ConstraintsPanelComponent } from '../../editor/panels/constraints-panel.component';
import { LayersPanelComponent } from '../../editor/panels/layers-panel.component';
import { OutputsPanelComponent } from '../../editor/panels/outputs-panel.component';
import { ProjectPanelComponent } from '../../editor/panels/project-panel.component';
import { StylePanelComponent } from '../../editor/panels/style-panel.component';
import { readDraft } from '../../editor/scene-saver';
import { TimelineComponent } from '../../editor/timeline/timeline.component';
import { ToolPaletteComponent } from '../../editor/toolbar/tool-palette.component';
import { saveDraftToAccount } from '../draft-handoff';

type Mode = 'owned' | 'draft' | 'shared';

/** The editor (D16, D18): tools on the left, the canvas, panels on the right (a bottom sheet on a phone), the timeline. */
@Component({
  selector: 'app-editor-page',
  imports: [
    NgTemplateOutlet, FormsModule, BsAlertComponent, BsButtonTypeDirective, BsCheckboxComponent, BsFormComponent, BsFormControlDirective, BsModalComponent, BsModalHeaderDirective,
    BsModalFooterDirective, BsOffcanvasComponent, BsOffcanvasHeaderDirective, BsSpinnerComponent, BsTabControlComponent, BsTabPageComponent, BsTabPageHeaderDirective,
    SparkIconComponent, EditorCanvasComponent, ToolPaletteComponent, StylePanelComponent, LayersPanelComponent, OutputsPanelComponent, ConstraintsPanelComponent,
    ProjectPanelComponent, TimelineComponent,
  ],
  providers: [EditorStore],
  templateUrl: './editor-page.component.html',
  styleUrl: './editor-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export default class EditorPageComponent {
  protected readonly store = inject(EditorStore);
  private readonly api = inject(IconicApi);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly auth = inject(SparkAuthService);
  protected readonly colors = Color;

  protected readonly loading = signal(true);
  protected readonly loadError = signal<string | null>(null);
  protected readonly snapshots = signal<SnapshotInfo[]>([]);
  protected readonly isPublic = signal(false);
  protected readonly takenDown = signal(false);
  protected readonly signedIn = computed(() => this.auth.isAuthenticated());
  protected readonly panelsOpen = signal(false);
  protected readonly phone = signal(false);
  protected readonly reportOpen = signal(false);
  protected readonly reportReason = signal('');
  protected readonly reportSent = signal(false);
  protected readonly actionError = signal<string | null>(null);
  protected readonly working = signal(false);
  protected readonly showTimeline = computed(() => this.store.kind() === 'Animation' || this.store.kind() === 'Cursor' || Object.keys(this.store.scene().animation.tracks).length > 0);

  protected readonly saveLabel = computed(() => {
    switch (this.store.saver.state()) {
      case 'dirty': return 'Unsaved changes';
      case 'saving': return 'Saving…';
      case 'saved': return this.store.mode() === 'draft' ? 'Saved in this browser' : 'Saved';
      case 'conflict': return 'Not saved: changed elsewhere';
      case 'quota': return 'Not saved: storage full';
      case 'error': return 'Not saved: retrying';
      default: return '';
    }
  });

  private mode: Mode = 'draft';
  private lastMeta: { width: number; height: number } | null = null;

  constructor() {
    const media = window.matchMedia('(max-width: 767.98px)');
    this.phone.set(media.matches);
    const onMedia = (e: MediaQueryListEvent) => this.phone.set(e.matches);
    media.addEventListener('change', onMedia);

    const sub = this.route.paramMap.subscribe(() => void this.open());
    inject(DestroyRef).onDestroy(() => {
      sub.unsubscribe();
      media.removeEventListener('change', onMedia);
      void this.store.saver.flush().finally(() => this.store.saver.stop());
    });

    // The project's width and height (for the list) follow the design size.
    effect(() => {
      const { width, height } = this.store.scene();
      untracked(() => {
        const key = this.store.key();
        if (this.store.mode() !== 'owned' || !key || !this.lastMeta) return;
        if (this.lastMeta.width === width && this.lastMeta.height === height) return;
        this.lastMeta = { width, height };
        void this.api.updateMeta(key, { width, height }).catch(() => { /* metadata only: the next change retries */ });
      });
    });
  }

  @HostListener('window:beforeunload', ['$event'])
  onBeforeUnload(e: BeforeUnloadEvent): void {
    const state = this.store.saver.state();
    if (state === 'dirty' || state === 'saving' || state === 'error') {
      void this.store.saver.flush();
      e.preventDefault();
    }
  }

  /** Opens what the route names; also "Reload" after a version conflict (no full page reload). */
  async open(): Promise<void> {
    this.mode = (this.route.snapshot.data['mode'] as Mode) ?? 'draft';
    this.loading.set(true);
    this.loadError.set(null);
    this.store.saver.stop();
    try {
      if (this.mode === 'owned') await this.openOwned(this.route.snapshot.paramMap.get('key')!);
      else if (this.mode === 'shared') await this.openShared(this.route.snapshot.paramMap.get('token')!);
      else this.openDraft();
    } catch (e) {
      const err = apiError(e);
      this.loadError.set(err.status === 404 ? 'This drawing does not exist, or it is not yours.' : err.message);
    } finally {
      this.loading.set(false);
    }
  }

  private async openOwned(key: string): Promise<void> {
    const project = await this.api.get(key);
    this.store.mode.set('owned');
    this.store.key.set(key);
    this.store.shareToken.set(project.shareToken);
    this.store.name.set(project.name);
    this.store.kind.set(project.kind);
    this.snapshots.set(project.snapshots);
    this.isPublic.set(project.isPublic);
    this.takenDown.set(project.shareTakenDown);
    const scene = normalizeScene(project.scene);
    this.lastMeta = { width: project.width, height: project.height };
    // startOwned first: loading sets the scene, and the saver must already know it is the saved one.
    this.store.saver.startOwned(project.version, scene, project.usage);
    this.store.load(scene);
  }

  private async openShared(token: string): Promise<void> {
    const shared = await this.api.shared(token);
    this.store.mode.set('shared');
    this.store.key.set(null);
    this.store.shareToken.set(token);
    this.store.name.set(shared.name);
    this.store.kind.set(shared.kind);
    this.store.tool.set('pan');
    this.store.load(normalizeScene(shared.scene));
  }

  private openDraft(): void {
    const draft = readDraft();
    this.store.mode.set('draft');
    this.store.key.set(null);
    this.store.shareToken.set(null);
    this.store.saver.startDraft();
    if (draft) {
      this.store.name.set(draft.name);
      this.store.kind.set(draft.kind);
      this.store.load(normalizeScene(draft.scene));
    } else {
      this.store.load(normalizeScene(null));
    }
  }

  saveMeta(meta: { name?: string; kind?: 'Icon' | 'Cursor' | 'Image' | 'Animation' }): void {
    const key = this.store.key();
    if (this.store.mode() === 'draft') { this.store.saver.changed(this.store.scene()); return; }
    if (!key) return;
    void this.api.updateMeta(key, meta).catch(e => this.actionError.set(apiError(e).message));
  }

  async handOff(): Promise<void> {
    await this.act(async () => {
      await this.store.saver.flush();
      // Stopped, so leaving this page does not write the draft back after the hand-off cleared it.
      this.store.saver.stop();
      try {
        const key = await saveDraftToAccount(this.api);
        if (key) await this.router.navigate(['/p', key]);
        else this.store.saver.startDraft();
      } catch (e) {
        this.store.saver.startDraft();
        throw e;
      }
    });
  }

  async remix(): Promise<void> {
    const token = this.store.shareToken();
    if (!token) return;
    await this.act(async () => {
      const result = await this.api.remix(token);
      await this.router.navigate(['/p', result.key]);
    });
  }

  async sendReport(): Promise<void> {
    const token = this.store.shareToken();
    const reason = this.reportReason().trim();
    if (!token || reason.length < 3) return;
    await this.act(async () => {
      await this.api.report(token, reason.slice(0, 1000));
      this.reportSent.set(true);
    });
  }

  closeReport(): void {
    this.reportOpen.set(false);
    this.reportSent.set(false);
    this.reportReason.set('');
  }

  private async act(work: () => Promise<void>): Promise<void> {
    this.actionError.set(null);
    this.working.set(true);
    try {
      await work();
    } catch (e) {
      this.actionError.set(apiError(e).message);
    } finally {
      this.working.set(false);
    }
  }
}
