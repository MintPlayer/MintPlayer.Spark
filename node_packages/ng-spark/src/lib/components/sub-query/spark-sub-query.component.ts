import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, Type } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router, RouterModule } from '@angular/router';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsTableComponent } from '@mintplayer/ng-bootstrap/table';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { SparkService } from '../../services/spark.service';
import { ResolveTranslationPipe } from '../../pipes/resolve-translation.pipe';
import { TranslateKeyPipe } from '../../pipes/translate-key.pipe';
import { AttributeValuePipe } from '../../pipes/attribute-value.pipe';
import { NgComponentOutlet } from '@angular/common';
import { SPARK_ATTRIBUTE_RENDERERS } from '../../providers/spark-attribute-renderer-registry';
import { EntityType, EntityAttributeDefinition } from '../../models/entity-type';
import { LookupReference } from '../../models/lookup-reference';
import { PersistentObject } from '../../models/persistent-object';
import { SparkQuery } from '../../models/spark-query';
import { ShowedOn, hasShowedOnFlag } from '../../models/showed-on';

@Component({
  selector: 'spark-sub-query',
  imports: [CommonModule, NgComponentOutlet, RouterModule, BsCardComponent, BsCardHeaderComponent, BsTableComponent, BsSpinnerComponent, ResolveTranslationPipe, TranslateKeyPipe, AttributeValuePipe],
  templateUrl: './spark-sub-query.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class SparkSubQueryComponent {
  private readonly router = inject(Router);
  private readonly sparkService = inject(SparkService);
  private readonly rendererRegistry = inject(SPARK_ATTRIBUTE_RENDERERS);

  queryId = input.required<string>();
  parentId = input.required<string>();
  parentType = input.required<string>();

  query = signal<SparkQuery | null>(null);
  entityType = signal<EntityType | null>(null);
  allEntityTypes = signal<EntityType[]>([]);
  items = signal<PersistentObject[]>([]);
  lookupReferenceOptions = signal<Record<string, LookupReference>>({});
  loading = signal(true);
  canRead = signal(false);

  visibleAttributes = computed(() => {
    return this.entityType()?.attributes
      .filter(a => a.isVisible && hasShowedOnFlag(a.showedOn, ShowedOn.Query))
      .sort((a, b) => a.order - b.order) || [];
  });

  constructor() {
    effect(() => {
      const qId = this.queryId();
      const pId = this.parentId();
      const pType = this.parentType();
      if (qId && pId && pType) {
        this.loadData(qId, pId, pType);
      }
    });
  }

  private async loadData(queryId: string, parentId: string, parentType: string): Promise<void> {
    this.loading.set(true);
    try {
      const [resolvedQuery, entityTypes] = await Promise.all([
        this.sparkService.getQuery(queryId),
        this.sparkService.getEntityTypes()
      ]);

      this.query.set(resolvedQuery);
      this.allEntityTypes.set(entityTypes);

      // Resolve entity type from query's entityType field
      if (resolvedQuery.entityType) {
        const et = entityTypes.find(t =>
          t.name === resolvedQuery.entityType || t.alias === resolvedQuery.entityType?.toLowerCase()
        );
        this.entityType.set(et || null);
        if (et) {
          const permissions = await this.sparkService.getPermissions(et.id);
          this.canRead.set(permissions.canRead);
        }
      }

      // Execute the query with parent context
      const sortDirection = resolvedQuery.sortDirection === 'desc' ? 'desc' : 'asc';
      const items = await this.sparkService.executeQuery(
        resolvedQuery.id,
        resolvedQuery.sortBy,
        sortDirection,
        parentId,
        parentType
      );
      this.items.set(items);

      this.loadLookupReferenceOptions();
    } catch {
      this.items.set([]);
    } finally {
      this.loading.set(false);
    }
  }

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
    this.lookupReferenceOptions.set(entries.reduce((acc, [k, v]) => ({ ...acc, [k]: v }), {} as Record<string, LookupReference>));
  }

  getColumnRendererComponent(attr: EntityAttributeDefinition): Type<any> | null {
    if (!attr.renderer) return null;
    return this.rendererRegistry.find(r => r.name === attr.renderer)?.columnComponent ?? null;
  }

  getColumnRendererInputs(item: PersistentObject, attr: EntityAttributeDefinition): Record<string, any> {
    const itemAttr = item.attributes.find(a => a.name === attr.name);
    return {
      value: itemAttr?.value,
      attribute: attr,
      options: attr.rendererOptions,
    };
  }

  onRowClick(item: PersistentObject): void {
    if (!this.canRead()) return;
    const et = this.entityType();
    if (et) {
      this.router.navigate(['/po', et.alias || et.id, item.id]);
    }
  }
}
