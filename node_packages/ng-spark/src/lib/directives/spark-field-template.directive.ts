import { Directive, input, TemplateRef } from '@angular/core';
import { EntityAttributeDefinition } from '../models/entity-type';
import { TranslatedString } from '../models/translated-string';

export interface SparkFieldTemplateContext {
  $implicit: EntityAttributeDefinition;
  formData: Record<string, any>;
  value: any;
  hasError: boolean;
  errorMessage: TranslatedString | null;
}

@Directive({
  selector: '[sparkFieldTemplate]',
  standalone: true
})
export class SparkFieldTemplateDirective {
  name = input.required<string>({ alias: 'sparkFieldTemplate' });

  constructor(public template: TemplateRef<SparkFieldTemplateContext>) {}
}
