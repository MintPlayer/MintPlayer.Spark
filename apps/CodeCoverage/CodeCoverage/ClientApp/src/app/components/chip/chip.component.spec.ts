import { Component, provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { ChipColor, ChipComponent } from './chip.component';

@Component({
  imports: [ChipComponent],
  template: `<app-chip class="ms-1" data-testid="chip" [color]="color()">paused</app-chip>`,
})
class HostComponent {
  readonly color = signal<ChipColor>('secondary');
}

describe('ChipComponent', () => {
  let fixture: ComponentFixture<HostComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();

    fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
  });

  const chip = () => fixture.nativeElement.querySelector('[data-testid="chip"]') as HTMLElement;
  const classes = () => [...chip().classList].sort().join(' ');

  // Bootstrap's own pair for both themes (merge-queue PRD §0 D75, D84): never a text-bg utility or a bare .badge.
  it('draws its colour as the subtle background with emphasis text and a subtle border, next to the caller\'s classes', () => {
    expect(chip().tagName).toBe('APP-CHIP');
    expect(chip().textContent).toBe('paused');
    expect(classes()).toBe('bg-secondary-subtle border border-secondary-subtle ms-1 text-secondary-emphasis');
  });

  it('swaps the whole pair when its colour changes, keeping the caller\'s classes', async () => {
    fixture.componentInstance.color.set('warning');
    fixture.detectChanges();
    await fixture.whenStable();

    expect(classes()).toBe('bg-warning-subtle border border-warning-subtle ms-1 text-warning-emphasis');
  });
});
