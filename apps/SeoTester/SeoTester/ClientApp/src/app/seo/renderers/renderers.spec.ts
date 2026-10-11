import { TestBed } from '@angular/core/testing';
import { SeoRunProgressComponent, seoRunProgress } from './run-progress.component';
import { SeoVerificationComponent, seoVerificationProofs } from './verification.component';
import { SeoCategoryScoresComponent } from './category-scores.component';
import { seoPageFindings } from './page-findings.component';
import { SeoPreviewsComponent, breadcrumbUrl, cut, socialCard } from './previews.component';
import { SPEC_CHECKS, provideSeoSpecServices } from '../testing';

describe('seoRunProgress', () => {
  it('fills towards the page limit while the crawl discovers more pages than it may fetch', () => {
    expect(seoRunProgress({ Status: 'Running', Phase: 'crawl', PagesDone: 25, PagesDiscovered: 400, MaxPages: 100 }))
      .toMatchObject({ running: true, percent: 25, total: 100, phase: 'crawl', step: 2 });
  });

  it('is waiting while queued, and over once the run finished', () => {
    expect(seoRunProgress({ Status: 'Queued', Phase: 'queued' })).toMatchObject({ running: true, phase: null, percent: 0 });
    expect(seoRunProgress({ Status: 'Completed', Phase: 'completed', PagesDone: 12 })).toMatchObject({ running: false, done: 12 });
  });
});

describe('SeoRunProgressComponent', () => {
  beforeEach(() => TestBed.configureTestingModule({ providers: provideSeoSpecServices() }));

  async function render(formData: Record<string, unknown>) {
    const fixture = TestBed.createComponent(SeoRunProgressComponent);
    fixture.componentRef.setInput('formData', formData);
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
  }

  it('shows the stage, the pages and the page being fetched while running', async () => {
    const el = await render({ Status: 'Running', Phase: 'crawl', PagesDone: 3, PagesDiscovered: 9, MaxPages: 100, CurrentUrl: 'https://example.com/x' });
    expect(el.querySelector('[data-running]')!.textContent).toContain('seoUi.phase.crawl');
    expect(el.textContent).toContain('3 / 9');
    expect(el.textContent).toContain('https://example.com/x');
  });

  it('says why a run that was blocked by robots.txt has no report', async () => {
    const el = await render({ Status: 'Blocked', Phase: 'blocked' });
    expect(el.textContent).toContain('seoUi.blocked');
    expect(el.querySelector('[data-running]')).toBeNull();
  });
});

describe('seoVerificationProofs', () => {
  it('spells the three proofs exactly as the server matches them (OwnershipProof)', () => {
    expect(seoVerificationProofs('https://example.com/', 'example.com', 'abc123')).toEqual({
      dnsName: '_seotester.example.com',
      dnsValue: 'seotester-site-verification=abc123',
      fileUrl: 'https://example.com/.well-known/seotester-verification.txt',
      fileContent: 'seotester-site-verification=abc123',
      metaTag: '<meta name="seotester-site-verification" content="abc123">',
    });
  });
});

describe('SeoVerificationComponent', () => {
  beforeEach(() => TestBed.configureTestingModule({ providers: provideSeoSpecServices() }));

  async function render(value: string, formData: Record<string, unknown>) {
    const fixture = TestBed.createComponent(SeoVerificationComponent);
    fixture.componentRef.setInput('value', value);
    fixture.componentRef.setInput('formData', formData);
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
  }

  it('gives an unverified site the three methods, each value with a copy button', async () => {
    const el = await render('tok', { Origin: 'https://example.com', Host: 'example.com', IsVerified: false });
    expect([...el.querySelectorAll('[data-method]')].map(e => e.getAttribute('data-method'))).toEqual(['dns', 'file', 'meta']);
    expect(el.querySelectorAll('[data-unverified] button').length).toBe(5);
    expect(el.textContent).toContain('_seotester.example.com');
  });

  it('tells a verified site that its re-checks are failing', async () => {
    const el = await render('tok', { Origin: 'https://example.com', IsVerified: true, VerificationMethod: 'Dns', VerificationFailures: 2 });
    expect(el.querySelector('[data-verified]')).not.toBeNull();
    expect(el.querySelector('[data-failures]')!.textContent).toContain('2');
    expect(el.querySelector('[data-unverified]')).toBeNull();
  });
});

describe('SeoCategoryScoresComponent', () => {
  beforeEach(() => TestBed.configureTestingModule({ providers: provideSeoSpecServices() }));

  it('orders the categories as the catalogue does and says what was not measured or capped', async () => {
    const fixture = TestBed.createComponent(SeoCategoryScoresComponent);
    fixture.componentRef.setInput('value', [
      { Category: 'Security', Score: 72 }, { Category: 'Seo', Score: 49, Capped: true }, { Category: 'Performance', Score: null },
    ]);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;

    expect([...el.querySelectorAll('[data-category]')].map(e => e.getAttribute('data-category'))).toEqual(['Seo', 'Performance', 'Security']);
    expect(el.querySelector('[data-category="Seo"] [data-capped]')).not.toBeNull();
    expect(el.querySelector('[data-category="Performance"]')!.textContent).toContain('seoUi.notMeasured');
    expect(el.querySelectorAll('spark-threshold-bar').length).toBe(2);
  });
});

describe('seoPageFindings', () => {
  it('fills the summary with the page\'s own values and puts failures first', () => {
    const rows = seoPageFindings([
      { CheckId: 'seo.title-present', Status: 'Pass' },
      { CheckId: 'performance.cwv', Status: 'Fail', Detail: '{"value":"4.1 s"}' },
    ], new Map(SPEC_CHECKS.map(c => [c.id, c] as const)));
    expect(rows.map(r => r.id)).toEqual(['performance.cwv', 'seo.title-present']);
    expect(rows[0].summary).toBe('LCP is 4.1 s');
  });
});

describe('previews', () => {
  it('cuts where a result page would and shows the address as a breadcrumb', () => {
    expect(cut('a'.repeat(70), 60)).toHaveLength(60);
    expect(cut('a'.repeat(70), 60).endsWith('…')).toBe(true);
    expect(cut('short', 60)).toBe('short');
    expect(breadcrumbUrl('https://example.com/blog/post-1')).toBe('example.com › blog › post-1');
  });

  it('draws the card from the Open Graph values, falling back to the title, description and address (Q-M11d)', () => {
    const og = socialCard({ Title: 'T', Description: 'D', Url: 'https://example.com/a', OgTitle: 'OG T', OgDescription: 'OG D',
      OgUrl: 'https://www.example.com/a', OgImage: 'https://cdn.example.net/a.png' }, 'Runs/1/pages/x');
    expect(og).toMatchObject({ title: 'OG T', description: 'OG D', host: 'www.example.com', fromOpenGraph: true });
    expect(og.imageSrc).toBe('/seotester/og-image?page=Runs%2F1%2Fpages%2Fx');

    const plain = socialCard({ Title: 'T', Description: 'D', Url: 'https://example.com/a' }, 'Runs/1/pages/x');
    expect(plain).toMatchObject({ title: 'T', description: 'D', host: 'example.com', fromOpenGraph: false, imageUrl: '', imageSrc: null });
  });
});

describe('SeoPreviewsComponent', () => {
  beforeEach(() => TestBed.configureTestingModule({ providers: provideSeoSpecServices() }));

  async function render(formData: Record<string, unknown>) {
    const fixture = TestBed.createComponent(SeoPreviewsComponent);
    fixture.componentRef.setInput('value', 'Title');
    fixture.componentRef.setInput('formData', formData);
    fixture.componentRef.setInput('item', { id: 'Runs/1/pages/x' });
    await fixture.whenStable();
    return fixture;
  }

  it('loads the og:image only through the server proxy and shows its address as text', async () => {
    const fixture = await render({ Url: 'https://example.com/', OgImage: 'https://tracker.example.net/pixel.png' });
    const html = fixture.nativeElement as HTMLElement;

    const images = Array.from(html.querySelectorAll('img')).map(i => i.getAttribute('src'));
    expect(images).toEqual(['/seotester/og-image?page=Runs%2F1%2Fpages%2Fx']);
    expect(html.innerHTML).not.toContain('src="https://');
    expect(html.querySelector('.seo-card-image-url')?.textContent).toBe('https://tracker.example.net/pixel.png');
  });

  it('says why when the proxy refuses the image, and says when there is none', async () => {
    const fixture = await render({ Url: 'https://example.com/', OgImage: 'https://example.com/huge.png' });
    fixture.componentInstance.imageFailed.set(true);
    await fixture.whenStable();
    const html = fixture.nativeElement as HTMLElement;
    expect(html.querySelector('img')).toBeNull();
    expect(html.querySelector('.seo-card-image-failed')).not.toBeNull();

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: provideSeoSpecServices() });
    const none = await render({ Url: 'https://example.com/' });
    expect((none.nativeElement as HTMLElement).querySelector('img')).toBeNull();
    expect((none.nativeElement as HTMLElement).querySelector('.seo-card-no-image')).not.toBeNull();
  });
});
