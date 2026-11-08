import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsButtonTypeDirective } from '@mintplayer/ng-bootstrap/button-type';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsGridComponent } from '@mintplayer/ng-bootstrap/grid';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { SparkIconComponent } from '@mintplayer/ng-spark/icon';
import { CustomActionDefinition, EntityType, PersistentObject, confirmationText, detailBuiltInActions } from '@mintplayer/ng-spark/models';
import { ResolveTranslationPipe, TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkPoPresenterComponent, sparkObjectPage } from '@mintplayer/ng-spark/po-detail';
import { SparkReturnNavigationService, SparkService } from '@mintplayer/ng-spark/services';
import { SongHeaderComponent } from './song-header.component';

/**
 * A song's page (#601): the header with its thumbnail and play button, then the song's attributes. Spark's object
 * page loads it (canonical URL, SEO, server render: D19) and Spark's presenter draws the attributes (D20); the page
 * adds Edit and Delete by the same rules as Spark's detail page, and no custom actions (Song has none).
 */
@Component({
  selector: 'app-song-page',
  imports: [BsAlertComponent, BsButtonTypeDirective, BsGridComponent, BsSpinnerComponent, SparkIconComponent, SparkPoPresenterComponent,
    ResolveTranslationPipe, TranslateKeyPipe, SongHeaderComponent],
  templateUrl: './song-page.component.html',
  styleUrl: './song-page.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export default class SongPageComponent {
  private readonly router = inject(Router);
  private readonly sparkService = inject(SparkService);
  private readonly returnNavigation = inject(SparkReturnNavigationService);

  protected readonly colors = Color;
  protected readonly editAction = signal<CustomActionDefinition | null>(null);
  protected readonly deleteAction = signal<CustomActionDefinition | null>(null);

  // After the signals its callback sets: the route's first emission is synchronous.
  protected readonly page = sparkObjectPage({ beforePublish: (et, item) => this.loadRights(et, item) });

  /** Fetched before the song is published, so a reused page never shows the previous song's buttons. */
  private async loadRights(et: EntityType | null, item: PersistentObject): Promise<() => void> {
    const [permissions, actions] = et
      ? await Promise.all([this.sparkService.getPermissions(et.id), this.sparkService.getCustomActions(et.id)])
      : [null, null];
    return () => {
      const builtIns = permissions && actions ? detailBuiltInActions(actions, permissions, item) : null;
      this.editAction.set(builtIns?.edit ?? null);
      this.deleteAction.set(builtIns?.delete ?? null);
    };
  }

  protected onBack() {
    window.history.back();
  }

  protected onEdit() {
    this.router.navigate(['/po', this.page.type, this.page.id, 'edit']);
  }

  /** As on Spark's detail page: the version on screen is the one deleted (a row changed since is a 409). */
  protected async onDelete() {
    const etag = this.page.item()?.etag;
    if (!etag) return;
    const message = confirmationText(this.deleteAction(), 1);
    if (message && !confirm(message)) return;
    await this.sparkService.delete(this.page.type, this.page.id, etag);
    await this.returnNavigation.returnToList(this.page.entityType()?.name);
  }
}
