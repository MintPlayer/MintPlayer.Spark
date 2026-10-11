import { TestBed } from '@angular/core/testing';
import { SeoCheckResultsComponent, seoCheckRows } from './check-results.component';
import { SPEC_CHECKS, provideSeoSpecServices, stubCodeSnippet } from '../testing';

const catalogue = new Map(SPEC_CHECKS.map(c => [c.id, c] as const));

// The detail page hands nested persistent objects; the share page plain records. Both shapes are rows.
const results = [
  { attributes: [{ name: 'CheckId', value: 'performance.cwv' }, { name: 'Status', value: 'Error' }, { name: 'Note', value: 'PageSpeed Insights is not configured' }] },
  { CheckId: 'security.hsts', Status: 'Warn', Affected: 0, Delta: 'Unchanged', SampleUrls: [] },
  { CheckId: 'seo.title-present', Status: 'Fail', Affected: 3, Delta: 'New', SampleUrls: ['https://example.com/a', 'https://example.com/b'] },
  { CheckId: 'seo.unknown-check', Status: 'Pass', Delta: 'Fixed' },
];

describe('seoCheckRows', () => {
  it('joins the catalogue and puts failures first, then warnings, then the rest', () => {
    const rows = seoCheckRows(results, catalogue);
    // The rest by severity: the Medium check before the unknown one (Info).
    expect(rows.map(r => r.id)).toEqual(['seo.title-present', 'security.hsts', 'performance.cwv', 'seo.unknown-check']);
    expect(rows[0]).toMatchObject({ title: 'Page has a title', category: 'Seo', severity: 'Critical', affected: 3, delta: 'New', summary: '3 pages have no title' });
  });

  it('keeps a check without a catalogue entry, titled by its id, and drops a summary it cannot fill', () => {
    const rows = seoCheckRows(results, catalogue);
    expect(rows.find(r => r.id === 'seo.unknown-check')).toMatchObject({ title: 'seo.unknown-check', category: 'Seo', check: null });
    expect(rows.find(r => r.id === 'performance.cwv')?.summary).toBeNull();
  });

  it('ranks by status before severity: a failing Medium check comes before a warning High one', () => {
    const rows = seoCheckRows([{ CheckId: 'security.hsts', Status: 'Warn' }, { CheckId: 'performance.cwv', Status: 'Fail' }], catalogue);
    expect(rows.map(r => r.id)).toEqual(['performance.cwv', 'security.hsts']);
  });

  it('is empty for anything that is not a list', () => {
    expect(seoCheckRows(null, catalogue)).toEqual([]);
    expect(seoCheckRows('x', catalogue)).toEqual([]);
  });
});

describe('SeoCheckResultsComponent', () => {
  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({ providers: provideSeoSpecServices() });
    stubCodeSnippet();
  });

  async function render() {
    const fixture = TestBed.createComponent(SeoCheckResultsComponent);
    fixture.componentRef.setInput('value', results);
    await fixture.whenStable();
    fixture.detectChanges();
    return { fixture, el: fixture.nativeElement as HTMLElement };
  }

  it('shows the issues by category with what changed since the previous run', async () => {
    const { el } = await render();

    expect([...el.querySelectorAll('[data-check]')].map(e => e.getAttribute('data-check'))).toEqual(['seo.title-present', 'security.hsts']);
    expect([...el.querySelectorAll('section[data-category]')].map(e => e.getAttribute('data-category'))).toEqual(['Seo', 'Security']);
    expect(el.querySelector('[data-check="seo.title-present"] [data-delta="New"]')).not.toBeNull();
    expect(el.querySelector('[data-check="security.hsts"] [data-delta="Unchanged"]')).not.toBeNull();
    expect(el.querySelector('[data-check="seo.title-present"]')!.textContent).toContain('https://example.com/b');
    expect(el.querySelector('[data-filter="issues"]')!.textContent).toContain('(2)');
    expect(el.querySelector('[data-filter="notMeasured"]')!.textContent).toContain('(1)');
  });

  it('lists what was not measured with the reason', async () => {
    const { fixture, el } = await render();
    (el.querySelector('[data-filter="notMeasured"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    const row = el.querySelector('[data-check="performance.cwv"]')!;
    expect(row.querySelector('[data-note]')!.textContent).toContain('PageSpeed Insights is not configured');
    expect(row.textContent).toContain('Not measured');
  });

  it('opens how to fix per row, and remembers "show how to fix" as the default', async () => {
    const { fixture, el } = await render();
    expect(el.querySelector('[data-howtofix]')).toBeNull();

    (el.querySelector('[data-check="seo.title-present"] .btn-link') as HTMLButtonElement).click();
    fixture.detectChanges();
    const fix = el.querySelector('[data-check="seo.title-present"] [data-howtofix]')!;
    expect(fix.textContent).toContain('Add a <title> to every page.');
    expect(fix.querySelector('bs-code-snippet')!.textContent).toContain('<title>Home</title>');

    const toggle = el.querySelector('input[type=checkbox]') as HTMLInputElement;
    toggle.checked = true;
    toggle.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    expect(el.querySelectorAll('[data-howtofix]').length).toBe(2);
    expect(localStorage.getItem('seotester.howToFixOpen')).toBe('1');

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: provideSeoSpecServices() });
    stubCodeSnippet();
    const again = await render();
    expect(again.el.querySelectorAll('[data-howtofix]').length).toBe(2);
  });
});
