import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
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
import { EntityType, EntityAttributeDefinition, LookupReference, PersistentObject } from '../../core/models';
import { ShowedOn, hasShowedOnFlag } from '../../core/models/showed-on';
import { LanguageService } from '../../core/services/language.service';
import { TranslatePipe } from '../../core/pipes/translate.pipe';
import { TranslateKeyPipe } from '../../core/pipes/translate-key.pipe';
import { AttributeValuePipe } from '../../core/pipes/attribute-value.pipe';
import { AsDetailColumnsPipe } from '../../core/pipes/as-detail-columns.pipe';
import { AsDetailCellValuePipe } from '../../core/pipes/as-detail-cell-value.pipe';
import { IconComponent } from '../../components/icon/icon.component';
import { BsTableComponent } from '@mintplayer/ng-bootstrap/table';

@Component({
  selector: 'app-po-detail',
  imports: [CommonModule, RouterModule, BsAlertComponent, BsButtonGroupComponent, BsCardComponent, BsCardHeaderComponent, BsContainerComponent, BsGridComponent, BsGridRowDirective, BsGridColumnDirective, BsTableComponent, IconComponent, TranslatePipe, TranslateKeyPipe, AttributeValuePipe, AsDetailColumnsPipe, AsDetailCellValuePipe],
  templateUrl: './po-detail.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export default class PoDetailComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly sparkService = inject(SparkService);

  private readonly lang = inject(LanguageService);
  colors = Color;
  errorMessage = signal<string | null>(null);
  entityType = signal<EntityType | null>(null);
  allEntityTypes = signal<EntityType[]>([]);
  item = signal<PersistentObject | null>(null);
  lookupReferenceOptions: Record<string, LookupReference> = {};
  asDetailTypes: Record<string, EntityType> = {};
  asDetailReferenceOptions: Record<string, Record<string, PersistentObject[]>> = {};
  type = '';
  id = '';
  canEdit = signal(false);
  canDelete = signal(false);

  constructor() {
    this.route.paramMap.subscribe(params => this.onParamsChange(params));
  }

  private async onParamsChange(params: any): Promise<void> {
    this.type = params.get('type') || '';
    this.id = params.get('id') || '';

    try {
      const [entityTypes, item] = await Promise.all([
        this.sparkService.getEntityTypes(),
        this.sparkService.get(this.type, this.id)
      ]);
      this.allEntityTypes.set(entityTypes);
      this.entityType.set(entityTypes.find(t => t.id === this.type || t.alias === this.type) || null);
      this.item.set(item);
      this.loadLookupReferenceOptions();
      this.loadAsDetailTypes();

      if (this.entityType()) {
        const p = await this.sparkService.getPermissions(this.entityType()!.id);
        this.canEdit.set(p.canEdit);
        this.canDelete.set(p.canDelete);
      }
    } catch (e) {
      const error = e as HttpErrorResponse;
      this.errorMessage.set(error.error?.error || error.message || 'An unexpected error occurred');
    }
  }

  visibleAttributes = computed(() => {
    return this.entityType()?.attributes
      .filter(a => a.isVisible && hasShowedOnFlag(a.showedOn, ShowedOn.PersistentObject))
      .sort((a, b) => a.order - b.order) || [];
  });

  private async loadLookupReferenceOptions(): Promise<void> {
    const lookupAttrs = this.visibleAttributes().filter(a => a.lookupReferenceType);
    if (lookupAttrs.length === 0) return;

    const lookupNames = [...new Set(lookupAttrs.map(a => a.lookupReferenceType!))];
    const entries = await Promise.all(
      lookupNames.map(async name => {
        const result = await this.sparkService.getLookupReference(name);
        return [name, result] as const;
      })
    );

    this.lookupReferenceOptions = Object.fromEntries(entries);
  }

  private async loadAsDetailTypes(): Promise<void> {
    const asDetailAttrs = this.visibleAttributes().filter(a => a.dataType === 'AsDetail' && a.isArray && a.asDetailType);
    if (asDetailAttrs.length === 0) return;

    for (const attr of asDetailAttrs) {
      const asDetailType = this.allEntityTypes().find(t => t.clrType === attr.asDetailType);
      if (asDetailType) {
        this.asDetailTypes[attr.name] = asDetailType;
        const refCols = asDetailType.attributes.filter(a => a.dataType === 'Reference' && a.query);
        if (refCols.length > 0) {
          const refEntries = await Promise.all(
            refCols.filter(c => c.query).map(async col => {
              const results = await this.sparkService.executeQueryByName(col.query!);
              return [col.name, results] as const;
            })
          );
          this.asDetailReferenceOptions[attr.name] = Object.fromEntries(refEntries);
        }
      }
    }
  }

  getReferenceLinkRoute(referenceClrType: string, referenceId: any): string[] | null {
    if (!referenceId || !referenceClrType) return null;
    const targetType = this.allEntityTypes().find(t => t.clrType === referenceClrType);
    if (!targetType) return null;
    return ['/po', targetType.alias || targetType.id, referenceId];
  }

  getRawAttributeValue(attrName: string): any {
    return this.item()?.attributes.find(a => a.name === attrName)?.value;
  }

  getArrayValue(attrName: string): Record<string, any>[] {
    const attr = this.item()?.attributes.find(a => a.name === attrName);
    if (!attr || !Array.isArray(attr.value)) return [];
    return attr.value;
  }

  onEdit(): void {
    this.router.navigate(['/po', this.type, this.id, 'edit']);
  }

  async onDelete(): Promise<void> {
    if (confirm(this.lang.t('confirmDelete'))) {
      await this.sparkService.delete(this.type, this.id);
      this.router.navigate(['/']);
    }
  }

  onBack(): void {
    window.history.back();
  }
}
