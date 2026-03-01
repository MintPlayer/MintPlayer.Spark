import { Directive, input, TemplateRef } from '@angular/core';
import { EntityAttributeDefinition } from '../models/entity-type';
import { PersistentObject } from '../models/persistent-object';

export interface SparkColumnTemplateContext {
  $implicit: PersistentObject;
  attr: EntityAttributeDefinition;
  value: any;
}

@Directive({
  selector: '[sparkColumnTemplate]',
  standalone: true
})
export class SparkColumnTemplateDirective {
  name = input.required<string>({ alias: 'sparkColumnTemplate' });

  constructor(public template: TemplateRef<SparkColumnTemplateContext>) {}
}
