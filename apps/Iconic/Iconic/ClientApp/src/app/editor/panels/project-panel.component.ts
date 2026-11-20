import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsButtonTypeDirective } from '@mintplayer/ng-bootstrap/button-type';
import { BsFormComponent, BsFormControlDirective } from '@mintplayer/ng-bootstrap/form';
import { BsListGroupComponent, BsListGroupItemComponent } from '@mintplayer/ng-bootstrap/list-group';
import { BsProgressBarComponent, BsProgressComponent } from '@mintplayer/ng-bootstrap/progress-bar';
import { BsSelectComponent, BsSelectOption } from '@mintplayer/ng-bootstrap/select';
import { SparkIconComponent } from '@mintplayer/ng-spark/icon';
import { apiError, IconicApi, PROJECT_KINDS, type ProjectKind, type SnapshotInfo } from '../../api/iconic-api';
import { canEncodeWebp, download, exportScene, type ExportFormat } from '../core/export';
import { importImage } from '../core/import';
import { normalizeScene, type Id } from '../core/model';
import { enabledOutputs } from '../core/render';
import { EditorStore } from '../editor-store';

interface FormatButton { format: ExportFormat; label: string; animated?: boolean; single?: boolean }

const FORMATS: Record<ProjectKind, FormatButton[]> = {
  Icon: [{ format: 'ico', label: '.ico' }, { format: 'png', label: 'PNG', single: true }, { format: 'svg', label: 'SVG' }],
  Cursor: [{ format: 'cur', label: '.cur' }, { format: 'ani', label: '.ani (animated)', animated: true }, { format: 'png', label: 'PNG', single: true }],
  Image: [{ format: 'png', label: 'PNG', single: true }, { format: 'svg', label: 'SVG' }, { format: 'ico', label: '.ico' }],
  Animation: [
    { format: 'gif', label: 'GIF', animated: true, single: true },
    { format: 'apng', label: 'APNG', animated: true, single: true },
    { format: 'webp', label: 'WebP', animated: true, single: true },
    { format: 'png', label: 'PNG (rest pose)', single: true },
  ],
};

/** The drawing as a whole: name, kind, export (D1, D6), import (D9), snapshots (D22), sharing (D20) and quota (D21). */
@Component({
  selector: 'app-project-panel',
  imports: [
    FormsModule, BsAlertComponent, BsButtonTypeDirective, BsFormComponent, BsFormControlDirective, BsListGroupComponent, BsListGroupItemComponent,
    BsProgressComponent, BsProgressBarComponent, BsSelectComponent, BsSelectOption, SparkIconComponent,
  ],
  templateUrl: './project-panel.component.html',
  styleUrl: './panels.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectPanelComponent {
  protected readonly store = inject(EditorStore);
  private readonly api = inject(IconicApi);
  protected readonly colors = Color;
  protected readonly kinds = PROJECT_KINDS;

  readonly snapshots = input<SnapshotInfo[]>([]);
  readonly isPublic = input(false);
  readonly takenDown = input(false);
  readonly signedIn = input(false);
  /** The page handles these: they navigate or reload. */
  readonly saveDraftToAccount = output<void>();
  readonly remix = output<void>();
  readonly report = output<void>();
  readonly snapshotsChange = output<SnapshotInfo[]>();
  readonly shareChange = output<{ isPublic: boolean; shareToken: string | null }>();
  readonly metaChange = output<{ name?: string; kind?: ProjectKind }>();

  protected readonly busy = signal<string | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly notice = signal<string | null>(null);
  protected readonly snapshotName = signal('');
  protected readonly outputId = signal<Id | null>(null);
  protected readonly webp = signal(true);

  protected readonly formats = computed(() => FORMATS[this.store.kind()].filter(f => f.format !== 'webp' || this.webp()));
  protected readonly singleOutputs = computed(() => enabledOutputs(this.store.scene()).map(o => ({ id: o.id, label: `${o.output.width} × ${o.output.height}` })));
  protected readonly shareUrl = computed(() => {
    const token = this.store.shareToken();
    return token ? `${location.origin}/s/${encodeURIComponent(token)}` : null;
  });
  protected readonly usage = computed(() => this.store.saver.usage());

  constructor() {
    void canEncodeWebp().then(ok => this.webp.set(ok));
  }

  setName(name: string): void {
    const trimmed = name.trim().slice(0, 100);
    if (!trimmed || trimmed === this.store.name()) return;
    this.store.name.set(trimmed);
    this.metaChange.emit({ name: trimmed });
  }

  setKind(kind: ProjectKind): void {
    if (kind === this.store.kind()) return;
    this.store.kind.set(kind);
    this.metaChange.emit({ kind });
  }

  async export(f: FormatButton): Promise<void> {
    this.error.set(null);
    this.busy.set(`Exporting ${f.label}…`);
    // Let the spinner paint before the (synchronous) rendering starts.
    await new Promise(r => setTimeout(r, 30));
    try {
      const store = this.store;
      const file = await exportScene(store.scene(), f.format, store.name(), {
        text: store.fonts,
        paint: store.paint,
        paintDataUrl: (layer, width) => (store.paint.has(layer, width) ? (store.paint.get(layer, width) as HTMLCanvasElement).toDataURL('image/png') : null),
      }, f.single ? this.outputId() ?? undefined : undefined);
      download(file);
    } catch (e) {
      this.error.set(e instanceof Error ? e.message : String(e));
    } finally {
      this.busy.set(null);
    }
  }

  async import(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;
    this.error.set(null);
    try {
      const bytes = new Uint8Array(await file.arrayBuffer());
      // The import builds data URLs; an owned project's saver uploads them as attachments on the next save.
      const scene = structuredClone(this.store.scene());
      const result = await importImage(scene, file.name, bytes);
      this.store.change(s => Object.assign(s, scene));
      // Upload only once the pixels are in the paint store: reading them earlier would save an empty bitmap.
      await this.store.paint.loadAll(this.store.scene());
      for (const size of result.sizes) this.store.saver.paintChanged(result.layer, size);
      this.store.activeLayerChoice.set(result.layer);
      this.notice.set(`Imported ${result.sizes.length} size${result.sizes.length === 1 ? '' : 's'} (${result.sizes.join(', ')} px) as a paint layer.`);
    } catch (e) {
      this.error.set(e instanceof Error ? e.message : String(e));
    }
  }

  async createSnapshot(): Promise<void> {
    const key = this.store.key();
    if (!key) return;
    await this.run('Saving a snapshot…', async () => {
      await this.store.saver.flush();
      const result = await this.api.createSnapshot(key, this.snapshotName().trim() || new Date().toLocaleString());
      this.snapshotsChange.emit(result.snapshots);
      this.store.saver.usage.set(result.usage);
      this.snapshotName.set('');
    });
  }

  async restoreSnapshot(snapshot: SnapshotInfo): Promise<void> {
    const key = this.store.key();
    if (!key || !confirm(`Restore "${snapshot.name}"? The drawing as it is now is replaced (take a snapshot first to keep it).`)) return;
    await this.run('Restoring…', async () => {
      await this.store.saver.flush();
      const result = await this.api.restoreSnapshot(key, snapshot.id);
      const scene = normalizeScene(result.scene);
      this.store.load(scene);
      this.store.saver.startOwned(result.version, scene, result.usage);
      this.snapshotsChange.emit(result.snapshots);
    });
  }

  async deleteSnapshot(snapshot: SnapshotInfo): Promise<void> {
    const key = this.store.key();
    if (!key || !confirm(`Delete the snapshot "${snapshot.name}"?`)) return;
    await this.run('Deleting…', async () => {
      const result = await this.api.deleteSnapshot(key, snapshot.id);
      this.snapshotsChange.emit(result.snapshots);
      this.store.saver.usage.set(result.usage);
    });
  }

  async share(enabled: boolean, newLink = false): Promise<void> {
    const key = this.store.key();
    if (!key) return;
    await this.run(enabled ? 'Sharing…' : 'Unsharing…', async () => {
      const result = await this.api.share(key, enabled, newLink);
      this.store.shareToken.set(result.shareToken);
      this.shareChange.emit(result);
    });
  }

  async copyLink(): Promise<void> {
    const url = this.shareUrl();
    if (!url) return;
    try {
      await navigator.clipboard.writeText(url);
      this.notice.set('Link copied.');
    } catch {
      this.notice.set(url);
    }
  }

  protected percent(used: number, max: number): number {
    return max > 0 ? Math.min(100, (100 * used) / max) : 0;
  }

  protected size(bytes: number): string {
    return bytes >= 1024 * 1024 ? `${(bytes / 1024 / 1024).toFixed(1)} MB` : `${Math.ceil(bytes / 1024)} KB`;
  }

  private async run(label: string, work: () => Promise<void>): Promise<void> {
    this.error.set(null);
    this.busy.set(label);
    try {
      await work();
    } catch (e) {
      this.error.set(apiError(e).message);
    } finally {
      this.busy.set(null);
    }
  }
}
