import { TestBed } from '@angular/core/testing';
import { describe, expect, it, beforeEach } from 'vitest';

import { SparkParagraphRendererComponent } from './paragraph-renderer.component';
import { SPARK_ATTRIBUTE_RENDERERS, provideSparkAttributeRenderers } from './spark-attribute-renderer-registry';

/**
 * The core `paragraph` renderer (generic passkeys page, D5): escaped text by default, and an opt-out
 * that still goes through Angular's sanitizer, never around it.
 */
describe('SparkParagraphRendererComponent', () => {
    beforeEach(() => TestBed.resetTestingModule());

    function render(value: unknown, options?: Record<string, any>) {
        const fixture = TestBed.createComponent(SparkParagraphRendererComponent);
        fixture.componentRef.setInput('value', value);
        if (options !== undefined) fixture.componentRef.setInput('options', options);
        fixture.detectChanges();
        return fixture.nativeElement as HTMLElement;
    }

    it('escapes markup by default: it is shown as text, never parsed', () => {
        const host = render('<b>bold</b> <img src=x onerror="alert(1)">');

        expect(host.querySelector('b')).toBeNull();
        expect(host.querySelector('img')).toBeNull();
        expect(host.textContent).toContain('<b>bold</b>');
        expect(host.textContent).toContain('onerror="alert(1)"');
    });

    it('keeps line breaks: a blank line starts a paragraph, a newline is a <br>', () => {
        const host = render('First line\nsecond line\n\nNext paragraph');

        const paragraphs = host.querySelectorAll('p');
        expect(paragraphs.length).toBe(2);
        expect(paragraphs[0].querySelectorAll('br').length).toBe(1);
        expect(paragraphs[0].textContent).toBe('First linesecond line');
        expect(paragraphs[1].textContent).toBe('Next paragraph');
    });

    it('renders nothing for an empty value', () => {
        expect(render(null).querySelectorAll('p').length).toBe(0);
        expect(render('').querySelectorAll('p').length).toBe(0);
    });

    it('anything but an explicit sanitize:false still escapes', () => {
        expect(render('<i>x</i>', { sanitize: true }).querySelector('i')).toBeNull();
        expect(render('<i>x</i>', { sanitize: 'false' }).querySelector('i')).toBeNull();
    });

    it('sanitize:false renders markup through Angular\'s sanitizer, which strips scripts and handlers', () => {
        const host = render(
            '<p>Use a <strong>passkey</strong>.</p><script>window.__pwned = true</script><img src="x" onerror="window.__pwned = true">',
            { sanitize: false },
        );

        expect(host.querySelector('strong')?.textContent).toBe('passkey');
        expect(host.querySelector('script')).toBeNull();
        expect(host.innerHTML).not.toContain('onerror');
        expect((window as any).__pwned).toBeUndefined();
    });
});

describe('core renderers', () => {
    beforeEach(() => TestBed.resetTestingModule());

    it('paragraph is available without any registration, full width', () => {
        const paragraph = TestBed.inject(SPARK_ATTRIBUTE_RENDERERS).find(r => r.name === 'paragraph');

        expect(paragraph?.detailComponent).toBe(SparkParagraphRendererComponent);
        expect(paragraph?.fullWidth).toBe(true);
    });

    it('an app registration keeps the core ones, and its own of the same name wins', () => {
        class AppParagraph {}
        TestBed.configureTestingModule({
            providers: [provideSparkAttributeRenderers([{ name: 'paragraph', detailComponent: AppParagraph as any }])],
        });

        const registry = TestBed.inject(SPARK_ATTRIBUTE_RENDERERS);
        expect(registry.find(r => r.name === 'paragraph')?.detailComponent).toBe(AppParagraph);
        expect(registry.some(r => r.detailComponent === SparkParagraphRendererComponent)).toBe(true);
    });
});
