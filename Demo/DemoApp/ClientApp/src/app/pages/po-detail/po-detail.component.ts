import { ChangeDetectorRef, Component, inject, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { BsButtonGroupComponent } from '@mintplayer/ng-bootstrap/button-group'
import { SparkService } from '../../core/services/spark.service';
import { EntityType, PersistentObject } from '../../core/models';
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
  item: PersistentObject | null = null;
  type: string = '';
  id: string = '';

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
    return attr?.breadcrumb || attr?.value || '';
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
