import { Component, computed, inject, signal } from '@angular/core';
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
import { CustomActionDefinition, EntityType, LookupReference, PersistentObject } from '../../core/models';
import { ShowedOn, hasShowedOnFlag } from '../../core/models/showed-on';
import { IconComponent } from '../../components/icon/icon.component';
import { BsTableComponent } from '@mintplayer/ng-bootstrap/table';
import { firstValueFrom } from 'rxjs';
import { LanguageService } from '../../core/services/language.service';
import { TranslatePipe } from '../../core/pipes/translate.pipe';
import { TranslateKeyPipe } from '../../core/pipes/translate-key.pipe';
import { AttributeValuePipe } from '../../core/pipes/attribute-value.pipe';
import { AsDetailColumnsPipe } from '../../core/pipes/as-detail-columns.pipe';
import { AsDetailCellValuePipe } from '../../core/pipes/as-detail-cell-value.pipe';

@Component({
  selector: 'app-po-detail',
  imports: [CommonModule, RouterModule, BsAlertComponent, BsButtonGroupComponent, BsCardComponent, BsCardHeaderComponent, BsContainerComponent, BsGridComponent, BsGridRowDirective, BsGridColumnDirective, BsTableComponent, IconComponent, TranslatePipe, TranslateKeyPipe, AttributeValuePipe, AsDetailColumnsPipe, AsDetailCellValuePipe],
  templateUrl: './po-detail.component.html'
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
  lookupReferenceOptions = signal<Record<string, LookupReference>>({});
  asDetailTypes = signal<Record<string, EntityType>>({});
  asDetailReferenceOptions = signal<Record<string, Record<string, PersistentObject[]>>>({});
  type = '';
  id = '';
  canEdit = signal(false);
  canDelete = signal(false);
  customActions = signal<CustomActionDefinition[]>([]);

  constructor() {
    this.init();
  }

  private async init(): Promise<void> {
    const params = await firstValueFrom(this.route.paramMap);
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

      const et = this.entityType();
      if (et) {
        const [permissions, actions] = await Promise.all([
          this.sparkService.getPermissions(et.id),
          this.sparkService.getCustomActions(et.id)
        ]);
        this.canEdit.set(permissions.canEdit);
        this.canDelete.set(permissions.canDelete);
        this.customActions.set(actions.filter(a => a.showedOn === 'detail' || a.showedOn === 'both'));
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
    this.lookupReferenceOptions.set(Object.fromEntries(entries));
  }

  private async loadAsDetailTypes(): Promise<void> {
    const asDetailAttrs = this.visibleAttributes().filter(a => a.dataType === 'AsDetail' && a.isArray && a.asDetailType);
    if (asDetailAttrs.length === 0) return;

    const types = this.allEntityTypes();
    const newAsDetailTypes: Record<string, EntityType> = {};

    for (const attr of asDetailAttrs) {
      const asDetailType = types.find(t => t.clrType === attr.asDetailType);
      if (asDetailType) {
        newAsDetailTypes[attr.name] = asDetailType;
        const refCols = asDetailType.attributes.filter(a => a.dataType === 'Reference' && a.query);
        if (refCols.length > 0) {
          const refEntries = await Promise.all(
            refCols.map(async col => {
              const results = await this.sparkService.executeQueryByName(col.query!);
              return [col.name, results] as const;
            })
          );
          this.asDetailReferenceOptions.update(prev => ({
            ...prev,
            [attr.name]: Object.fromEntries(refEntries)
          }));
        }
      }
    }
    this.asDetailTypes.set(newAsDetailTypes);
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

  async onCustomAction(action: CustomActionDefinition): Promise<void> {
    if (action.confirmationMessageKey) {
      const message = this.lang.t(action.confirmationMessageKey) || 'Are you sure?';
      if (!confirm(message)) return;
    }
    try {
      await this.sparkService.executeCustomAction(this.type, action.name, this.item() || undefined);
      if (action.refreshOnCompleted) {
        const item = await this.sparkService.get(this.type, this.id);
        this.item.set(item);
      }
    } catch (e) {
      const err = e as HttpErrorResponse;
      this.errorMessage.set(err.error?.error || err.message || 'Action failed');
    }
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
