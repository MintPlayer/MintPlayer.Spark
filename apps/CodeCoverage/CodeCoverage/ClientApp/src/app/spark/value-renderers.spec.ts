import { provideZonelessChangeDetection, Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import type { PersistentObject } from '@mintplayer/ng-spark/models';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { DateTimeRendererComponent } from './date-time-renderer.component';
import { ConnectedRendererComponent } from './connected-renderer.component';
import { CoverageBarRendererComponent } from './coverage-bar-renderer.component';
import { CoverageDeltaRendererComponent } from './coverage-delta-renderer.component';
import { CoverageSummaryDetailRendererComponent } from './coverage-summary-detail-renderer.component';
import { AccountCoverageRendererComponent } from './account-coverage-renderer.component';
import { BuildSessionsRendererComponent } from './build-sessions-renderer.component';

/** The renderers that draw their own cell value, with no sibling-attribute reads. */

function po(attributes: Record<string, unknown>): PersistentObject {
  return {
    attributes: Object.entries(attributes).map(([name, value]) => ({ name, value })),
  } as unknown as PersistentObject;
}

function render<T>(type: Type<T>, inputs: Record<string, unknown>): ComponentFixture<T> {
  const fixture = TestBed.createComponent(type);
  for (const [name, value] of Object.entries(inputs)) fixture.componentRef.setInput(name, value);
  fixture.detectChanges();
  return fixture;
}

function text(fixture: ComponentFixture<unknown>): string {
  return (fixture.nativeElement as HTMLElement).textContent!.replace(/\s+/g, ' ').trim();
}

beforeEach(async () => {
  await TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      // The real service fetches /spark/culture from its constructor. Keys echo back, so an
      // assertion on 'app.connected' proves which key the template asked for.
      { provide: SparkLanguageService, useValue: { t: (key: string) => key } },
    ],
  }).compileComponents();
});

describe('DateTimeRendererComponent', () => {
  // A local-time string (no offset), so the expectation does not depend on the runner's zone.
  it('formats in the viewer locale with the medium format by default', () => {
    expect(text(render(DateTimeRendererComponent, { value: '2026-08-15T13:02:10' }))).toBe('Aug 15, 2026, 1:02:10 PM');
  });

  it('honours rendererOptions.format', () => {
    expect(text(render(DateTimeRendererComponent, { value: '2026-08-15T13:02:10', options: { format: 'yyyy-MM-dd' } }))).toBe('2026-08-15');
  });

  it('ignores a non-string format', () => {
    expect(text(render(DateTimeRendererComponent, { value: '2026-08-15T13:02:10', options: { format: 3 } }))).toBe('Aug 15, 2026, 1:02:10 PM');
  });

  it('renders nothing for an absent value', () => {
    for (const value of [null, undefined, '']) {
      expect(text(render(DateTimeRendererComponent, { value }))).toBe('');
    }
  });

  // formatDate throws on an unparseable value; the raw value beats a broken cell.
  it('falls back to the raw value when it cannot be parsed as a date', () => {
    expect(text(render(DateTimeRendererComponent, { value: 'not a date' }))).toBe('not a date');
  });
});

describe('ConnectedRendererComponent', () => {
  it('draws the green connected badge only for a literal true', () => {
    const fixture = render(ConnectedRendererComponent, { value: true });

    const badge = (fixture.nativeElement as HTMLElement).querySelector('bs-badge')!;
    expect(badge.classList.contains('text-bg-success')).toBe(true);
    expect(text(fixture)).toBe('app.connected');
  });

  it('draws the grey not-connected badge for false, null and truthy non-booleans', () => {
    for (const value of [false, null, undefined, 'true', 1]) {
      const fixture = render(ConnectedRendererComponent, { value });
      const badge = (fixture.nativeElement as HTMLElement).querySelector('bs-badge')!;
      expect(badge.classList.contains('text-bg-secondary')).toBe(true);
      expect(text(fixture)).toBe('app.notConnected');
    }
  });
});

describe('CoverageBarRendererComponent', () => {
  it('draws the bar from an AsDetail PersistentObject value', () => {
    const fixture = render(CoverageBarRendererComponent, { value: po({ LinesCovered: 3, LinesCoverable: 4, FilesCount: 1 }) });

    expect(fixture.componentInstance.summary()!.linesCovered).toBe(3);
    expect(text(fixture)).toContain('75.0%');
  });

  it('draws the dash when there is no summary', () => {
    for (const value of [null, po({ LinesCoverable: 0, FilesCount: 0 })]) {
      const fixture = render(CoverageBarRendererComponent, { value });
      expect(fixture.componentInstance.summary()).toBeNull();
      expect(text(fixture)).toContain('—');
    }
  });
});

describe('CoverageSummaryDetailRendererComponent', () => {
  it('draws the bar plus line, branch and file counts', () => {
    const fixture = render(CoverageSummaryDetailRendererComponent, {
      value: po({ LinesCovered: 50, LinesCoverable: 200, BranchesCovered: 3, BranchesTotal: 8, FilesCount: 12 }),
    });

    expect(text(fixture)).toContain('25.0%');
    expect(text(fixture)).toContain('50/200 lines · 3/8 branches · 12 files');
  });

  it('accepts the flat camelCase shape the /api endpoints deliver', () => {
    const fixture = render(CoverageSummaryDetailRendererComponent, {
      value: { linesCovered: 1, linesCoverable: 2, branchesCovered: 0, branchesTotal: 0, filesCount: 1 },
    });

    expect(text(fixture)).toContain('1/2 lines');
  });

  it('draws only a dash for no data or a value of the wrong shape', () => {
    for (const value of [null, undefined, { unrelated: true }]) {
      const fixture = render(CoverageSummaryDetailRendererComponent, { value });
      expect(text(fixture)).toBe('—');
      expect((fixture.nativeElement as HTMLElement).querySelector('app-coverage-bar')).toBeNull();
    }
  });
});

describe('CoverageDeltaRendererComponent', () => {
  function span(fixture: ComponentFixture<unknown>): HTMLElement {
    return (fixture.nativeElement as HTMLElement).querySelector('span')!;
  }

  it('shows a rise signed and green', () => {
    const fixture = render(CoverageDeltaRendererComponent, { value: 2.345 });

    expect(span(fixture).textContent).toBe('+2.3');
    expect(span(fixture).classList.contains('text-success')).toBe(true);
    expect(span(fixture).classList.contains('text-danger')).toBe(false);
  });

  it('shows a drop red', () => {
    const fixture = render(CoverageDeltaRendererComponent, { value: -1 });

    expect(span(fixture).textContent).toBe('-1.0');
    expect(span(fixture).classList.contains('text-danger')).toBe(true);
  });

  it('shows no change as muted 0.0 and no reference as a muted dash', () => {
    const zero = render(CoverageDeltaRendererComponent, { value: 0 });
    expect(span(zero).textContent).toBe('0.0');
    expect(span(zero).classList.contains('text-muted')).toBe(true);

    for (const value of [null, undefined, '', 'n/a']) {
      const none = render(CoverageDeltaRendererComponent, { value });
      expect(span(none).textContent).toBe('—');
      expect(span(none).classList.contains('text-muted')).toBe(true);
    }
  });

  it('parses a numeric string', () => {
    expect(span(render(CoverageDeltaRendererComponent, { value: '1.5' })).textContent).toBe('+1.5');
  });
});

describe('AccountCoverageRendererComponent', () => {
  it('appends the unit the number column would lose', () => {
    expect(text(render(AccountCoverageRendererComponent, { value: 85.3 }))).toBe('85.3%');
    expect(text(render(AccountCoverageRendererComponent, { value: '42' }))).toBe('42%');
  });

  // 0% is a measurement; it must not collapse into the "nothing measured" dash.
  it('keeps a real zero', () => {
    const fixture = render(AccountCoverageRendererComponent, { value: 0 });

    expect(text(fixture)).toBe('0%');
    expect((fixture.nativeElement as HTMLElement).querySelector('.text-muted')).toBeNull();
  });

  it('shows a muted dash for no value or a non-number', () => {
    for (const value of [null, undefined, '', 'abc']) {
      const fixture = render(AccountCoverageRendererComponent, { value });
      expect(text(fixture)).toBe('—');
      expect((fixture.nativeElement as HTMLElement).querySelector('.text-muted')).not.toBeNull();
    }
  });
});

describe('BuildSessionsRendererComponent', () => {
  function lines(fixture: ComponentFixture<unknown>): HTMLElement[] {
    return Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('div.small'));
  }

  function badges(line: HTMLElement): HTMLElement[] {
    return Array.from(line.querySelectorAll('bs-badge'));
  }

  it('draws one line per nested PersistentObject session with its flags and parse status', () => {
    const fixture = render(BuildSessionsRendererComponent, {
      value: [
        po({ SessionId: 's1', JobName: 'unit', Flags: ['dotnet', 'linux'], ParseStatus: 'Parsed' }),
        po({ SessionId: 's2', JobName: 'e2e', Flags: [], ParseStatus: 'Pending' }),
      ],
    });

    const [first, second] = lines(fixture);
    expect(lines(fixture)).toHaveLength(2);
    expect(first.textContent).toContain('unit');
    expect(badges(first).map(b => b.textContent!.trim())).toEqual(['dotnet', 'linux', 'Parsed']);
    expect(badges(first)[2].classList.contains('text-bg-success')).toBe(true);
    expect(badges(second)[0].classList.contains('text-bg-warning')).toBe(true);
    expect(first.querySelector('.text-danger')).toBeNull();
  });

  it('accepts flat dict sessions, shows a failure red with its error, and falls back to the session id as the name', () => {
    const fixture = render(BuildSessionsRendererComponent, {
      value: [{ SessionId: 's9', ParseStatus: 'Failed', Error: 'Malformed XML', Flags: 'not-an-array' }],
    });

    const [line] = lines(fixture);
    expect(line.textContent).toContain('s9');
    expect(badges(line)).toHaveLength(1);
    expect(badges(line)[0].classList.contains('text-bg-danger')).toBe(true);
    expect(line.querySelector('span.text-danger')!.textContent).toBe('Malformed XML');
  });

  it('survives null entries in the array', () => {
    const fixture = render(BuildSessionsRendererComponent, { value: [null] });

    expect(lines(fixture)).toHaveLength(1);
    expect(fixture.componentInstance.sessions()[0]).toEqual({ key: '0', name: '', flags: [], parseStatus: '', error: undefined });
  });

  it('renders nothing when the value is not an array', () => {
    for (const value of [null, undefined, 'sessions', { SessionId: 's1' }]) {
      expect(lines(render(BuildSessionsRendererComponent, { value }))).toHaveLength(0);
    }
  });
});
