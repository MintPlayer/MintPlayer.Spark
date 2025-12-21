import { ChangeDetectorRef, Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsFormModule } from '@mintplayer/ng-bootstrap/form';
import { BsGridModule } from '@mintplayer/ng-bootstrap/grid';
import { BsButtonTypeDirective } from '@mintplayer/ng-bootstrap/button-type';
import { BsSelectModule } from '@mintplayer/ng-bootstrap/select';
import { SparkService } from '../../core/services/spark.service';
import { EntityType, EntityAttributeDefinition, PersistentObject, PersistentObjectAttribute } from '../../core/models';
import { switchMap, forkJoin, of } from 'rxjs';

@Component({
  selector: 'app-po-edit',
  standalone: true,
  imports: [CommonModule, FormsModule, BsFormModule, BsGridModule, BsButtonTypeDirective, BsSelectModule],
  templateUrl: './po-edit.component.html'
})
export default class PoEditComponent implements OnInit {
  colors = Color;
  entityType: EntityType | null = null;
  item: PersistentObject | null = null;
  type: string = '';
  id: string = '';
  formData: Record<string, any> = {};
  referenceOptions: Record<string, PersistentObject[]> = {};

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private sparkService: SparkService,
    private cdr: ChangeDetectorRef
  ) {}

  ngOnInit(): void {
    this.route.paramMap.pipe(
      switchMap(params => {
        this.type = params.get('type') || '';
        this.id = params.get('id') || '';
        return forkJoin({
          entityType: this.sparkService.getEntityTypes().pipe(
            switchMap(types => of(types.find(t => t.id === this.type) || null))
          ),
          item: this.sparkService.get(this.type, this.id)
        });
      })
    ).subscribe(result => {
      this.entityType = result.entityType;
      this.item = result.item;
      this.initFormData();
      this.loadReferenceOptions();
      this.cdr.detectChanges();
    });
  }

  initFormData(): void {
    this.formData = {};
    this.getEditableAttributes().forEach(attr => {
      const itemAttr = this.item?.attributes.find(a => a.name === attr.name);
      const defaultValue = attr.dataType === 'reference' ? null : '';
      this.formData[attr.name] = itemAttr?.value ?? defaultValue;
    });
  }

  loadReferenceOptions(): void {
    const refAttrs = this.getEditableAttributes().filter(a => a.dataType === 'reference' && a.query);

    if (refAttrs.length === 0) return;

    const queries: Record<string, ReturnType<typeof this.sparkService.executeQueryByName>> = {};
    refAttrs.forEach(attr => {
      if (attr.query) {
        queries[attr.name] = this.sparkService.executeQueryByName(attr.query);
      }
    });

    forkJoin(queries).subscribe(results => {
      this.referenceOptions = results;
      this.cdr.detectChanges();
    });
  }

  getReferenceOptions(attr: EntityAttributeDefinition): PersistentObject[] {
    return this.referenceOptions[attr.name] || [];
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
      objectTypeId: this.entityType.id,
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
