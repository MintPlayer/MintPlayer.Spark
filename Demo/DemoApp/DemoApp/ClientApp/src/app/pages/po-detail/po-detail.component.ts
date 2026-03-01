import { ChangeDetectionStrategy, Component, computed, inject, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsButtonGroupComponent } from '@mintplayer/ng-bootstrap/button-group';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsContainerComponent } from '@mintplayer/ng-bootstrap/container';
import { BsGridComponent, BsGridRowDirective, BsGridColumnDirective } from '@mintplayer/ng-bootstrap/grid';
import { SparkService } from '../../core/services/spark.service';
import { TranslationsService } from '../../core/services/translations.service';
import { TranslateKeyPipe } from '../../core/pipes/translate-key.pipe';
import { ResolveTranslationPipe } from '../../core/pipes/resolve-translation.pipe';
import { AttributeValuePipe } from '../../core/pipes/attribute-value.pipe';
import { AsDetailColumnsPipe } from '../../core/pipes/as-detail-columns.pipe';
import { AsDetailCellValuePipe } from '../../core/pipes/as-detail-cell-value.pipe';
import { ArrayValuePipe } from '../../core/pipes/array-value.pipe';
import { ReferenceLinkRoutePipe } from '../../core/pipes/reference-link-route.pipe';
import { RawAttributeValuePipe } from '../../core/pipes/raw-attribute-value.pipe';
import { EntityType, EntityAttributeDefinition, LookupReference, PersistentObject } from '../../core/models';
import { ShowedOn, hasShowedOnFlag } from '../../core/models/showed-on';
import { IconComponent } from '../../components/icon/icon.component';
import { BsTableComponent } from '@mintplayer/ng-bootstrap/table';
import { firstValueFrom } from 'rxjs';

@Component({
  selector: 'app-po-detail',
  imports: [CommonModule, RouterModule, BsAlertComponent, BsButtonGroupComponent, BsCardComponent, BsCardHeaderComponent, BsContainerComponent, BsGridComponent, BsGridRowDirective, BsGridColumnDirective, BsTableComponent, IconComponent, TranslateKeyPipe, ResolveTranslationPipe, AttributeValuePipe, AsDetailColumnsPipe, AsDetailCellValuePipe, ArrayValuePipe, ReferenceLinkRoutePipe, RawAttributeValuePipe],
  templateUrl: './po-detail.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export default class PoDetailComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly sparkService = inject(SparkService);
  private readonly translations = inject(TranslationsService);

  colors = Color;
  errorMessage = signal<string | null>(null);
  entityType = signal<EntityType | null>(null);
  allEntityTypes = signal<EntityType[]>([]);
  item = signal<PersistentObject | null>(null);
  lookupReferenceOptions = signal<Record<string, LookupReference>>({});
  asDetailTypes = signal<Record<string, EntityType>>({});
  asDetailReferenceOptions = signal<Record<string, Record<string, PersistentObject[]>>>({});
  type = signal('');
  id = signal('');
  canEdit = signal(false);
  canDelete = signal(false);

  visibleAttributes = computed(() => {
    return this.entityType()?.attributes
      .filter(a => a.isVisible && hasShowedOnFlag(a.showedOn, ShowedOn.PersistentObject))
      .sort((a, b) => a.order - b.order) || [];
  });

  async ngOnInit(): Promise<void> {
    const params = await firstValueFrom(this.route.paramMap);
    this.type.set(params.get('type') || '');
    this.id.set(params.get('id') || '');

    try {
      const [entityTypes, itemResult] = await Promise.all([
        this.sparkService.getEntityTypes(),
        this.sparkService.get(this.type(), this.id())
      ]);

      this.allEntityTypes.set(entityTypes);
      this.entityType.set(entityTypes.find(t => t.id === this.type() || t.alias === this.type()) || null);
      this.item.set(itemResult);
      this.loadLookupReferenceOptions();
      this.loadAsDetailTypes();

      if (this.entityType()) {
        const perms = await this.sparkService.getPermissions(this.entityType()!.id);
        this.canEdit.set(perms.canEdit);
        this.canDelete.set(perms.canDelete);
      }
    } catch (e) {
      const error = e as HttpErrorResponse;
      this.errorMessage.set(error.error?.error || error.message || 'An unexpected error occurred');
    }
  }

  private async loadLookupReferenceOptions(): Promise<void> {
    const lookupAttrs = this.visibleAttributes().filter(a => a.lookupReferenceType);
    if (lookupAttrs.length === 0) return;

    const lookupNames = [...new Set(lookupAttrs.map(a => a.lookupReferenceType!))];
    const entries = await Promise.all(
      lookupNames.map(async name => {
        const ref = await this.sparkService.getLookupReference(name);
        return [name, ref] as [string, LookupReference];
      })
    );
    this.lookupReferenceOptions.set(Object.fromEntries(entries));
  }

  private loadAsDetailTypes(): void {
    const asDetailAttrs = this.visibleAttributes().filter(a => a.dataType === 'AsDetail' && a.isArray && a.asDetailType);
    if (asDetailAttrs.length === 0) return;

    const newAsDetailTypes: Record<string, EntityType> = {};
    for (const attr of asDetailAttrs) {
      const asDetailType = this.allEntityTypes().find(t => t.clrType === attr.asDetailType);
      if (asDetailType) {
        newAsDetailTypes[attr.name] = asDetailType;
        const refCols = asDetailType.attributes.filter(a => a.dataType === 'Reference' && a.query);
        if (refCols.length > 0) {
          this.loadAsDetailRefOptions(attr.name, refCols);
        }
      }
    }
    this.asDetailTypes.set(newAsDetailTypes);
  }

  private async loadAsDetailRefOptions(attrName: string, refCols: EntityAttributeDefinition[]): Promise<void> {
    const entries = await Promise.all(
      refCols.filter(c => c.query).map(async col => {
        const items = await this.sparkService.executeQueryByName(col.query!);
        return [col.name, items] as [string, PersistentObject[]];
      })
    );
    this.asDetailReferenceOptions.update(prev => ({ ...prev, [attrName]: Object.fromEntries(entries) }));
  }

  onEdit(): void {
    this.router.navigate(['/po', this.type(), this.id(), 'edit']);
  }

  async onDelete(): Promise<void> {
    if (confirm(this.translations.t('confirmDelete'))) {
      await this.sparkService.delete(this.type(), this.id());
      this.router.navigate(['/']);
    }
  }

  onBack(): void {
    window.history.back();
  }
}
