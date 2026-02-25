import { ChangeDetectorRef, Component, inject, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertModule } from '@mintplayer/ng-bootstrap/alert';
import { BsButtonGroupComponent } from '@mintplayer/ng-bootstrap/button-group';
import { BsCardModule } from '@mintplayer/ng-bootstrap/card';
import { BsContainerComponent } from '@mintplayer/ng-bootstrap/container';
import { BsGridModule } from '@mintplayer/ng-bootstrap/grid';
import { SparkService } from '../../core/services/spark.service';
import { CustomActionDefinition, EntityType, EntityAttributeDefinition, LookupReference, PersistentObject } from '../../core/models';
import { ShowedOn, hasShowedOnFlag } from '../../core/models/showed-on';
import { IconComponent } from '../../components/icon/icon.component';
import { BsTableComponent } from '@mintplayer/ng-bootstrap/table';
import { switchMap, forkJoin, of } from 'rxjs';
import { LanguageService } from '../../core/services/language.service';
import { TranslatePipe } from '../../core/pipes/translate.pipe';
import { TranslateKeyPipe } from '../../core/pipes/translate-key.pipe';

@Component({
  selector: 'app-po-detail',
  imports: [CommonModule, RouterModule, BsAlertModule, BsButtonGroupComponent, BsCardModule, BsContainerComponent, BsGridModule, BsTableComponent, IconComponent, TranslatePipe, TranslateKeyPipe],
  templateUrl: './po-detail.component.html'
})
export default class PoDetailComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly sparkService = inject(SparkService);
  private readonly cdr = inject(ChangeDetectorRef);

  private readonly lang = inject(LanguageService);
  colors = Color;
  errorMessage: string | null = null;
  entityType: EntityType | null = null;
  allEntityTypes: EntityType[] = [];
  item: PersistentObject | null = null;
  lookupReferenceOptions: Record<string, LookupReference> = {};
  asDetailTypes: Record<string, EntityType> = {};
  asDetailReferenceOptions: Record<string, Record<string, PersistentObject[]>> = {};
  type: string = '';
  id: string = '';
  canEdit = false;
  canDelete = false;
  customActions: CustomActionDefinition[] = [];

  ngOnInit(): void {
    this.route.paramMap.pipe(
      switchMap(params => {
        this.type = params.get('type') || '';
        this.id = params.get('id') || '';
        return forkJoin({
          entityTypes: this.sparkService.getEntityTypes(),
          item: this.sparkService.get(this.type, this.id)
        });
      })
    ).subscribe({
      next: result => {
        this.allEntityTypes = result.entityTypes;
        this.entityType = result.entityTypes.find(t => t.id === this.type || t.alias === this.type) || null;
        this.item = result.item;
        this.loadLookupReferenceOptions();
        this.loadAsDetailTypes();
        this.cdr.detectChanges();
        if (this.entityType) {
          this.sparkService.getPermissions(this.entityType.id).subscribe(p => {
            this.canEdit = p.canEdit;
            this.canDelete = p.canDelete;
            this.cdr.detectChanges();
          });
          this.sparkService.getCustomActions(this.entityType.id).subscribe(actions => {
            this.customActions = actions.filter(a => a.showedOn === 'detail' || a.showedOn === 'both');
            this.cdr.detectChanges();
          });
        }
      },
      error: (error: HttpErrorResponse) => {
        this.errorMessage = error.error?.error || error.message || 'An unexpected error occurred';
        this.cdr.detectChanges();
      }
    });
  }

  getVisibleAttributes() {
    return this.entityType?.attributes
      .filter(a => a.isVisible && hasShowedOnFlag(a.showedOn, ShowedOn.PersistentObject))
      .sort((a, b) => a.order - b.order) || [];
  }

  getAttributeValue(attrName: string): any {
    const attr = this.item?.attributes.find(a => a.name === attrName);
    if (!attr) return '';

    // For Reference attributes, breadcrumb is resolved on backend
    if (attr.breadcrumb) return attr.breadcrumb;

    // For AsDetail attributes, format using displayFormat
    const attrDef = this.entityType?.attributes.find(a => a.name === attrName);
    if (attrDef?.dataType === 'AsDetail' && attr.value) {
      if (Array.isArray(attr.value)) {
        return `${attr.value.length} item${attr.value.length !== 1 ? 's' : ''}`;
      }
      if (typeof attr.value === 'object') {
        return this.formatAsDetailValue(attrDef, attr.value);
      }
    }

    // For LookupReference attributes, resolve to translated display name
    if (attrDef?.lookupReferenceType && attr.value != null && attr.value !== '') {
      const lookupRef = this.lookupReferenceOptions[attrDef.lookupReferenceType];
      if (lookupRef) {
        const option = lookupRef.values.find(v => v.key === String(attr.value));
        if (option) {
          return this.lang.resolve(option.values) || option.key;
        }
      }
    }

    // For boolean attributes, preserve null for indeterminate state
    if (attrDef?.dataType === 'boolean') {
      return attr.value ?? null;
    }

    return attr.value ?? '';
  }

  private loadLookupReferenceOptions(): void {
    const lookupAttrs = this.getVisibleAttributes().filter(a => a.lookupReferenceType);
    if (lookupAttrs.length === 0) return;

    const lookupNames = [...new Set(lookupAttrs.map(a => a.lookupReferenceType!))];
    const queries: Record<string, ReturnType<typeof this.sparkService.getLookupReference>> = {};
    lookupNames.forEach(name => {
      queries[name] = this.sparkService.getLookupReference(name);
    });

    forkJoin(queries).subscribe(results => {
      this.lookupReferenceOptions = results;
      this.cdr.detectChanges();
    });
  }

  private loadAsDetailTypes(): void {
    const asDetailAttrs = this.getVisibleAttributes().filter(a => a.dataType === 'AsDetail' && a.isArray && a.asDetailType);
    if (asDetailAttrs.length === 0) return;

    asDetailAttrs.forEach(attr => {
      const asDetailType = this.allEntityTypes.find(t => t.clrType === attr.asDetailType);
      if (asDetailType) {
        this.asDetailTypes[attr.name] = asDetailType;
        const refCols = asDetailType.attributes.filter(a => a.dataType === 'Reference' && a.query);
        if (refCols.length > 0) {
          const refQueries: Record<string, ReturnType<typeof this.sparkService.executeQueryByName>> = {};
          refCols.forEach(col => {
            if (col.query) {
              refQueries[col.name] = this.sparkService.executeQueryByName(col.query);
            }
          });
          forkJoin(refQueries).subscribe(results => {
            this.asDetailReferenceOptions[attr.name] = results;
            this.cdr.detectChanges();
          });
        }
      }
    });
  }

  getAsDetailColumns(attr: EntityAttributeDefinition): EntityAttributeDefinition[] {
    const type = this.asDetailTypes[attr.name];
    if (!type) return [];
    return type.attributes
      .filter(a => a.isVisible)
      .sort((a, b) => a.order - b.order);
  }

  getAsDetailCellValue(parentAttr: EntityAttributeDefinition, row: Record<string, any>, col: EntityAttributeDefinition): string {
    const value = row[col.name];
    if (value == null) return '';

    if (col.dataType === 'Reference' && col.query) {
      const parentOptions = this.asDetailReferenceOptions[parentAttr.name];
      if (parentOptions) {
        const options = parentOptions[col.name];
        if (options) {
          const match = options.find(o => o.id === value);
          if (match) return match.breadcrumb || match.name || String(value);
        }
      }
    }

    return String(value);
  }

  getReferenceLinkRoute(referenceClrType: string, referenceId: any): string[] | null {
    if (!referenceId || !referenceClrType) return null;
    const targetType = this.allEntityTypes.find(t => t.clrType === referenceClrType);
    if (!targetType) return null;
    return ['/po', targetType.alias || targetType.id, referenceId];
  }

  getRawAttributeValue(attrName: string): any {
    return this.item?.attributes.find(a => a.name === attrName)?.value;
  }

  getArrayValue(attrName: string): Record<string, any>[] {
    const attr = this.item?.attributes.find(a => a.name === attrName);
    if (!attr || !Array.isArray(attr.value)) return [];
    return attr.value;
  }

  private formatAsDetailValue(attrDef: EntityAttributeDefinition, value: Record<string, any>): string {
    // Find the AsDetail entity type
    const asDetailType = this.allEntityTypes.find(t => t.clrType === attrDef.asDetailType);

    // 1. Try displayFormat (template with {PropertyName} placeholders)
    if (asDetailType?.displayFormat) {
      const result = this.resolveDisplayFormat(asDetailType.displayFormat, value);
      if (result && result.trim()) return result;
    }

    // 2. Try displayAttribute (single property name)
    if (asDetailType?.displayAttribute && value[asDetailType.displayAttribute]) {
      return value[asDetailType.displayAttribute];
    }

    // 3. Fallback to common property names
    const displayProps = ['Name', 'Title', 'Street', 'name', 'title'];
    for (const prop of displayProps) {
      if (value[prop]) return value[prop];
    }

    return this.lang.t('notSet');
  }

  private resolveDisplayFormat(format: string, data: Record<string, any>): string {
    return format.replace(/\{(\w+)\}/g, (match, propertyName) => {
      const value = data[propertyName];
      return value != null ? String(value) : '';
    });
  }

  onCustomAction(action: CustomActionDefinition): void {
    if (action.confirmationMessageKey) {
      const message = this.lang.t(action.confirmationMessageKey) || 'Are you sure?';
      if (!confirm(message)) return;
    }
    this.sparkService.executeCustomAction(this.type, action.name, this.item || undefined).subscribe({
      next: () => {
        if (action.refreshOnCompleted) {
          this.sparkService.get(this.type, this.id).subscribe(item => {
            this.item = item;
            this.cdr.detectChanges();
          });
        }
      },
      error: (err) => {
        this.errorMessage = err.error?.error || err.message || 'Action failed';
        this.cdr.detectChanges();
      }
    });
  }

  onEdit(): void {
    this.router.navigate(['/po', this.type, this.id, 'edit']);
  }

  onDelete(): void {
    if (confirm(this.lang.t('confirmDelete'))) {
      this.sparkService.delete(this.type, this.id).subscribe(() => {
        this.router.navigate(['/']);
      });
    }
  }

  onBack(): void {
    window.history.back();
  }
}
