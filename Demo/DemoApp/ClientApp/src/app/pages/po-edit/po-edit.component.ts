import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { SparkService } from '../../core/services/spark.service';
import { EntityType, PersistentObject, PersistentObjectAttribute } from '../../core/models';
import { switchMap, forkJoin, of } from 'rxjs';

@Component({
  selector: 'app-po-edit',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './po-edit.component.html'
})
export default class PoEditComponent implements OnInit {
  entityType: EntityType | null = null;
  item: PersistentObject | null = null;
  type: string = '';
  id: string = '';
  formData: Record<string, any> = {};

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private sparkService: SparkService
  ) {}

  ngOnInit(): void {
    this.route.paramMap.pipe(
      switchMap(params => {
        this.type = params.get('type') || '';
        this.id = params.get('id') || '';
        return forkJoin({
          entityType: this.sparkService.getEntityTypes().pipe(
            switchMap(types => of(types.find(t => t.clrType === this.type) || null))
          ),
          item: this.sparkService.get(this.type, this.id)
        });
      })
    ).subscribe(result => {
      this.entityType = result.entityType;
      this.item = result.item;
      this.initFormData();
    });
  }

  initFormData(): void {
    this.formData = {};
    this.getEditableAttributes().forEach(attr => {
      const itemAttr = this.item?.attributes.find(a => a.name === attr.name);
      this.formData[attr.name] = itemAttr?.value || '';
    });
  }

  getEditableAttributes() {
    return this.entityType?.attributes
      .filter(a => a.isVisible && !a.isReadOnly)
      .sort((a, b) => a.order - b.order) || [];
  }

  getInputType(dataType: string): string {
    switch (dataType) {
      case 'number':
      case 'decimal':
        return 'number';
      case 'boolean':
        return 'checkbox';
      case 'datetime':
        return 'datetime-local';
      default:
        return 'text';
    }
  }

  onSave(): void {
    if (!this.entityType || !this.item) return;

    const attributes: PersistentObjectAttribute[] = this.item.attributes.map(attr => {
      const editableAttr = this.getEditableAttributes().find(a => a.name === attr.name);
      return {
        ...attr,
        value: editableAttr ? this.formData[attr.name] : attr.value
      };
    });

    const po: Partial<PersistentObject> = {
      id: this.item.id,
      name: this.formData['Name'] || this.item.name,
      clrType: this.type,
      attributes
    };

    this.sparkService.update(this.type, this.id, po).subscribe(() => {
      this.router.navigate(['/po', this.type, this.id]);
    });
  }

  onCancel(): void {
    this.router.navigate(['/po', this.type, this.id]);
  }
}
