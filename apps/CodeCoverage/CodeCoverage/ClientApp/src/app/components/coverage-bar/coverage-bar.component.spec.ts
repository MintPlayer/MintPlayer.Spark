import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { Color } from '@mintplayer/ng-bootstrap';
import { CoverageSummary } from '../../services/browse.service';
import { CoverageBarComponent } from './coverage-bar.component';

function summary(covered: number, coverable: number): CoverageSummary {
  return {
    linesCovered: covered,
    linesCoverable: coverable,
    branchesCovered: 0,
    branchesTotal: 0,
    filesCount: 1,
  } as CoverageSummary;
}

describe('CoverageBarComponent', () => {
  let fixture: ComponentFixture<CoverageBarComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [CoverageBarComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();

    fixture = TestBed.createComponent(CoverageBarComponent);
  });

  function withSummary(value: CoverageSummary | null) {
    fixture.componentRef.setInput('summary', value);
    fixture.detectChanges();
    return fixture.componentInstance;
  }

  it('renders the percentage for a measured summary', () => {
    const component = withSummary(summary(75, 100));

    expect(component.percent()).toBe(75);
    expect(fixture.nativeElement.textContent).toContain('75.0%');
  });

  // The dash is the visual form of "0/0 is no data". A zero-width green bar would
  // read as a real measurement.
  it('renders a dash rather than a bar when there is nothing to report', () => {
    const component = withSummary(null);

    expect(component.percent()).toBeNull();
    expect(fixture.nativeElement.textContent).toContain('—');
    expect(fixture.nativeElement.querySelector('bs-progress')).toBeNull();
  });

  it('colours by the same thresholds the badge uses', () => {
    expect(withSummary(summary(80, 100)).color()).toBe(Color.success);
    expect(withSummary(summary(60, 100)).color()).toBe(Color.warning);
    expect(withSummary(summary(59, 100)).color()).toBe(Color.danger);
  });

  it('falls back to a neutral colour when there is no percentage', () => {
    expect(withSummary(null).color()).toBe(Color.secondary);
  });
});
