import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsButtonTypeDirective } from '@mintplayer/ng-bootstrap/button-type';
import { BsCardBodyComponent, BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsFormComponent, BsFormControlDirective } from '@mintplayer/ng-bootstrap/form';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { SparkAuthService } from '@mintplayer/ng-spark/auth/core';
import { SparkIconComponent } from '@mintplayer/ng-spark/icon';
import { apiError, IconicApi, PROJECT_KINDS, type ProjectKind, type ProjectListItem } from '../../api/iconic-api';
import { emptyScene, newId, newOutput, type Scene } from '../../editor/core/model';
import { DRAFT_KEY, readDraft, type Draft } from '../../editor/scene-saver';
import { saveDraftToAccount } from '../draft-handoff';

const KIND_TEXT: Record<ProjectKind, string> = {
  Icon: 'An .ico with every size, from 16 to 256 px',
  Cursor: 'A .cur or animated .ani, with a hotspot',
  Image: 'A PNG or SVG at the size you choose',
  Animation: 'An animated GIF, APNG or WebP',
};

/** A new drawing's sizes per kind (D1): what each format is usually made of. */
export function newScene(kind: ProjectKind, width: number, height: number): Scene {
  switch (kind) {
    case 'Icon': return emptyScene(width, height, [16, 24, 32, 48, 64, 256]);
    case 'Cursor': return emptyScene(width, height, [32, 48, 64, 128]);
    default: {
      const scene = emptyScene(width, height, []);
      const id = newId('o');
      scene.outputs[id] = { ...newOutput(Math.min(1024, width)), height: Math.min(1024, height), strokeMode: 'scale' };
      scene.outputOrder = [id];
      return scene;
    }
  }
}

@Component({
  selector: 'app-home',
  imports: [
    DatePipe, FormsModule, RouterLink, BsAlertComponent, BsButtonTypeDirective, BsCardComponent, BsCardHeaderComponent, BsCardBodyComponent,
    BsFormComponent, BsFormControlDirective, BsSpinnerComponent, SparkIconComponent,
  ],
  templateUrl: './home.component.html',
  styleUrl: './home.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export default class HomeComponent {
  private readonly api = inject(IconicApi);
  private readonly auth = inject(SparkAuthService);
  private readonly router = inject(Router);
  protected readonly colors = Color;
  protected readonly kinds = PROJECT_KINDS;
  protected readonly kindText = KIND_TEXT;

  protected readonly signedIn = computed(() => this.auth.isAuthenticated());
  protected readonly projects = signal<ProjectListItem[] | null>(null);
  protected readonly maxProjects = signal(0);
  protected readonly error = signal<string | null>(null);
  protected readonly working = signal(false);
  protected readonly draft = signal<Draft | null>(readDraft());

  protected readonly name = signal('Untitled');
  protected readonly kind = signal<ProjectKind>('Icon');
  protected readonly width = signal(256);
  protected readonly height = signal(256);
  protected readonly full = computed(() => (this.projects()?.length ?? 0) >= this.maxProjects() && this.maxProjects() > 0);

  constructor() {
    effect(() => {
      if (this.signedIn()) untracked(() => void this.load());
      else this.projects.set(null);
    });
  }

  async load(): Promise<void> {
    try {
      const result = await this.api.list();
      this.projects.set(result.projects);
      this.maxProjects.set(result.maxProjects);
    } catch (e) {
      this.error.set(apiError(e).message);
    }
  }

  thumbnail(p: ProjectListItem): string | null {
    return p.hasThumbnail ? `${this.api.fileUrl(p.key, 'thumbnail.png')}?v=${encodeURIComponent(p.modifiedAt ?? '')}` : null;
  }

  setKind(kind: ProjectKind): void {
    this.kind.set(kind);
    // Cursors are small by nature; the others start at 256.
    const size = kind === 'Cursor' ? 64 : 256;
    this.width.set(size);
    this.height.set(size);
  }

  /** A signed-in user's drawing is created on the server; anyone else's lives in this browser (D3). */
  async create(): Promise<void> {
    const width = clampSize(this.width()), height = clampSize(this.height());
    const name = this.name().trim() || 'Untitled';
    const scene = newScene(this.kind(), width, height);
    if (!this.signedIn()) {
      if (this.draft() && !confirm('Start over? The drawing in this browser is replaced.')) return;
      const draft: Draft = { name, kind: this.kind(), scene, savedAt: new Date().toISOString() };
      try {
        localStorage.setItem(DRAFT_KEY, JSON.stringify(draft));
      } catch {
        this.error.set('This browser does not allow storing a drawing. Sign in to save it to your account.');
        return;
      }
      await this.router.navigate(['/draft']);
      return;
    }
    await this.run(async () => {
      const created = await this.api.create({ name, kind: this.kind(), width, height, scene });
      await this.router.navigate(['/p', created.key]);
    });
  }

  async remove(p: ProjectListItem): Promise<void> {
    if (!confirm(`Delete "${p.name}" with its snapshots? This cannot be undone.`)) return;
    await this.run(async () => {
      await this.api.delete(p.key);
      this.projects.update(list => list?.filter(x => x.key !== p.key) ?? null);
    });
  }

  async keepDraft(): Promise<void> {
    await this.run(async () => {
      const key = await saveDraftToAccount(this.api);
      this.draft.set(null);
      if (key) await this.router.navigate(['/p', key]);
    });
  }

  private async run(work: () => Promise<void>): Promise<void> {
    this.error.set(null);
    this.working.set(true);
    try {
      await work();
    } catch (e) {
      this.error.set(apiError(e).message);
    } finally {
      this.working.set(false);
    }
  }
}

function clampSize(v: number): number {
  return Number.isFinite(v) ? Math.max(8, Math.min(4096, Math.round(v))) : 256;
}
