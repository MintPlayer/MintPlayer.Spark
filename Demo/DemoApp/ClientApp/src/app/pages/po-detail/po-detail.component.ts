import { ChangeDetectorRef, Component, inject, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { BsButtonGroupComponent } from '@mintplayer/ng-bootstrap/button-group'
import { SparkService } from '../../core/services/spark.service';
import { EntityType, EntityAttributeDefinition, PersistentObject } from '../../core/models';
import { switchMap, forkJoin, of } from 'rxjs';

@Component({
  selector: 'app-po-detail',
  imports: [CommonModule, RouterModule, BsButtonGroupComponent],
  templateUrl: './po-detail.component.html'
})
export default class PoDetailComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly sparkService = inject(SparkService);
  private readonly cdr = inject(ChangeDetectorRef);

  entityType: EntityType | null = null;
  allEntityTypes: EntityType[] = [];
  item: PersistentObject | null = null;
  type: string = '';
  id: string = '';

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
    ).subscribe(result => {
      this.allEntityTypes = result.entityTypes;
      this.entityType = result.entityTypes.find(t => t.id === this.type) || null;
      this.item = result.item;
      this.cdr.detectChanges();
    });
  }

  getVisibleAttributes() {
    return this.entityType?.attributes
      .filter(a => a.isVisible)
      .sort((a, b) => a.order - b.order) || [];
  }

  getAttributeValue(attrName: string): any {
    const attr = this.item?.attributes.find(a => a.name === attrName);
    if (!attr) return '';

    // For Reference attributes, breadcrumb is resolved on backend
    if (attr.breadcrumb) return attr.breadcrumb;

    // For AsDetail attributes, format using displayFormat
    const attrDef = this.entityType?.attributes.find(a => a.name === attrName);
    if (attrDef?.dataType === 'AsDetail' && attr.value && typeof attr.value === 'object') {
      return this.formatAsDetailValue(attrDef, attr.value);
    }

    return attr.value || '';
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

    return '(object)';
  }

  private resolveDisplayFormat(format: string, data: Record<string, any>): string {
    return format.replace(/\{(\w+)\}/g, (match, propertyName) => {
      const value = data[propertyName];
      return value != null ? String(value) : '';
    });
  }

  onEdit(): void {
    this.router.navigate(['/po', this.type, this.id, 'edit']);
  }

  onDelete(): void {
    if (confirm('Are you sure you want to delete this item?')) {
      this.sparkService.delete(this.type, this.id).subscribe(() => {
        this.router.navigate(['/']);
      });
    }
  }

  onBack(): void {
    window.history.back();
  }
}
