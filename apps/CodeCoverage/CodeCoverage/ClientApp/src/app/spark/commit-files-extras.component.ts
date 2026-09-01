import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { SparkService } from '@mintplayer/ng-spark/services';
import type { PersistentObject } from '@mintplayer/ng-spark/models';
import { CommitFilesPanelComponent } from '../components/commit-files-panel/commit-files-panel.component';
import { valueFor } from '@mintplayer/ng-spark/models';

/**
 * Bridges a Commit PersistentObject to the shared Files panel: owner/name come
 * from the referenced Repository's FullName, loaded through the row-secured
 * Spark PO endpoint. (Deliberately NOT derived from the reference breadcrumb —
 * see the upstream breadcrumb-mismatch bug found on the seeded data.)
 */
@Component({
  selector: 'app-commit-files-extras',
  imports: [CommitFilesPanelComponent],
  template: `
    @if (target(); as t) {
      <app-commit-files-panel [owner]="t.owner" [name]="t.name" [sha]="t.sha" />
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class CommitFilesExtrasComponent {
  private readonly spark = inject(SparkService);

  po = input.required<PersistentObject>();

  readonly target = signal<{ owner: string; name: string; sha: string } | null>(null);

  constructor() {
    effect(async () => {
      const po = this.po();
      const sha = valueFor(po, 'Sha')?.value;
      const repoId = valueFor(po, 'Repository')?.value;
      if (typeof sha !== 'string' || !sha || typeof repoId !== 'string' || !repoId) {
        this.target.set(null);
        return;
      }
      try {
        const repo = await this.spark.get('Repository', repoId);
        const fullName = valueFor(repo, 'FullName')?.value;
        const [owner, name] = typeof fullName === 'string' ? fullName.split('/') : [];
        this.target.set(owner && name ? { owner, name, sha } : null);
      } catch {
        this.target.set(null);
      }
    });
  }
}
