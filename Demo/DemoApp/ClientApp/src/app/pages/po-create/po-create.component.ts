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
import { switchMap, of, forkJoin } from 'rxjs';

@Component({
  selector: 'app-po-create',
  standalone: true,
  imports: [CommonModule, FormsModule, BsFormModule, BsGridModule, BsButtonTypeDirective, BsSelectModule],
  templateUrl: './po-create.component.html'
})
export default class PoCreateComponent implements OnInit {
  colors = Color;
  entityType: EntityType | null = null;
  type: string = '';
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
        return this.sparkService.getEntityTypes().pipe(
          switchMap(types => of(types.find(t => t.id === this.type) || null))
        );
      })
    ).subscribe(entityType => {
      this.entityType = entityType;
      this.initFormData();
      this.loadReferenceOptions();
      this.cdr.detectChanges();
    });
  }

  initFormData(): void {
    this.formData = {};
    this.getEditableAttributes().forEach(attr => {
      this.formData[attr.name] = attr.dataType === 'reference' ? null : '';
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
    if (!this.entityType) return;

    const attributes: PersistentObjectAttribute[] = this.getEditableAttributes().map(attr => ({
      id: attr.id,
      name: attr.name,
      value: this.formData[attr.name],
      dataType: attr.dataType,
      isRequired: attr.isRequired,
      isVisible: attr.isVisible,
      isReadOnly: attr.isReadOnly,
      order: attr.order,
      rules: attr.rules
    }));

    const po: Partial<PersistentObject> = {
      name: this.formData['Name'] || 'New Item',
      objectTypeId: this.entityType.id,
      attributes
    };

    this.sparkService.create(this.type, po).subscribe(result => {
      this.router.navigate(['/po', this.type, result.id]);
    });
  }

  onCancel(): void {
    window.history.back();
  }
}
