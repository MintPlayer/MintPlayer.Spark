import { Pipe, PipeTransform } from '@angular/core';

@Pipe({ name: 'iconName', standalone: true, pure: true })
export class IconNamePipe implements PipeTransform {
  transform(value: string | undefined, fallback: string): string {
    const iconClass = value || fallback;
    // Strip 'bi-' prefix if present
    return iconClass.startsWith('bi-') ? iconClass.substring(3) : iconClass;
  }
}
