import { ChangeDetectorRef, Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { SparkService } from '../../core/services/spark.service';
import { EntityType, PersistentObject, PersistentObjectAttribute } from '../../core/models';
import { switchMap, of } from 'rxjs';

@Component({
  selector: 'app-po-create',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './po-create.component.html'
})
export default class PoCreateComponent implements OnInit {
  entityType: EntityType | null = null;
  type: string = '';
  formData: Record<string, any> = {};

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
      this.cdr.detectChanges();
    });
  }

  initFormData(): void {
    this.formData = {};
    this.getEditableAttributes().forEach(attr => {
      this.formData[attr.name] = '';
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
      clrType: this.entityType.clrType,
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
