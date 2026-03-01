import { Directive, input, TemplateRef } from '@angular/core';
import { EntityAttributeDefinition } from '../models/entity-type';
import { PersistentObject } from '../models/persistent-object';

export interface SparkDetailFieldTemplateContext {
  $implicit: EntityAttributeDefinition;
  item: PersistentObject;
  value: any;
}

@Directive({
  selector: '[sparkDetailFieldTemplate]',
  standalone: true
})
export class SparkDetailFieldTemplateDirective {
  name = input.required<string>({ alias: 'sparkDetailFieldTemplate' });

  constructor(public template: TemplateRef<SparkDetailFieldTemplateContext>) {}
}
