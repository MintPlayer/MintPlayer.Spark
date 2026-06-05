import { TestBed } from '@angular/core/testing';
import { DomSanitizer } from '@angular/platform-browser';
import { describe, expect, it, beforeEach } from 'vitest';

import { SparkIconComponent } from './spark-icon.component';
import { SparkIconRegistry } from '@mintplayer/ng-spark/services';

describe('SparkIconComponent', () => {
  let registry: SparkIconRegistry;
  let sanitizer: DomSanitizer;

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [SparkIconComponent] });
    registry = TestBed.inject(SparkIconRegistry);
    sanitizer = TestBed.inject(DomSanitizer);
  });

  it('renders the SVG span when the icon is registered', () => {
    registry.register('test-svg', sanitizer.bypassSecurityTrustHtml('<svg data-test="ok"><circle /></svg>'));

    const fixture = TestBed.createComponent(SparkIconComponent);
    fixture.componentRef.setInput('name', 'test-svg');
    fixture.detectChanges();

    const span = fixture.nativeElement.querySelector('span') as HTMLElement | null;
    expect(span).not.toBeNull();
    expect(span!.innerHTML).toContain('<svg');
  });

  it('falls back to <i class="bi bi-{name}"> when the icon is not registered', () => {
    const fixture = TestBed.createComponent(SparkIconComponent);
    fixture.componentRef.setInput('name', 'definitely-unregistered');
    fixture.detectChanges();

    const i = fixture.nativeElement.querySelector('i') as HTMLElement | null;
    expect(i).not.toBeNull();
    expect(i!.className).toContain('bi');
    expect(i!.className).toContain('bi-definitely-unregistered');

    const span = fixture.nativeElement.querySelector('span') as HTMLElement | null;
    expect(span).toBeNull();
  });

  it('switches between registered and fallback when input changes', () => {
    registry.register('icon-a', sanitizer.bypassSecurityTrustHtml('<svg id="a" />'));

    const fixture = TestBed.createComponent(SparkIconComponent);
    fixture.componentRef.setInput('name', 'icon-a');
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('span')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('i')).toBeNull();

    fixture.componentRef.setInput('name', 'unregistered');
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('span')).toBeNull();
    expect(fixture.nativeElement.querySelector('i')).not.toBeNull();
  });
});
