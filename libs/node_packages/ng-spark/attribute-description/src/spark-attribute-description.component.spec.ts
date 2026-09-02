import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { SparkAttributeDescriptionComponent } from './spark-attribute-description.component';
import { currentLanguage, TranslatedString } from '@mintplayer/ng-spark/models';

/**
 * #348 — the [i] beside an attribute label. The tooltip mechanics belong to ng-bootstrap; what is
 * pinned here is the contract every label site relies on: absent description → nothing rendered,
 * present → a focusable button named by the text, clicks that do not reach the header/label, and
 * text that follows the language switch.
 */
function create(description: TranslatedString | undefined) {
  TestBed.configureTestingModule({ providers: [provideNoopAnimations()] });
  const fixture = TestBed.createComponent(SparkAttributeDescriptionComponent);
  fixture.componentRef.setInput('description', description);
  fixture.detectChanges();
  return fixture;
}

describe('SparkAttributeDescriptionComponent', () => {
  afterEach(() => currentLanguage.set('en'));

  it('renders nothing without a description', () => {
    const fixture = create(undefined);

    expect(fixture.nativeElement.querySelector('button')).toBeNull();
  });

  it('renders nothing for a description that is empty in every language', () => {
    const fixture = create({ en: '   ' });

    expect(fixture.nativeElement.querySelector('button')).toBeNull();
  });

  it('renders a focusable button named by the description, with the info icon', () => {
    const fixture = create({ en: 'What this field is for.' });

    const button: HTMLButtonElement = fixture.nativeElement.querySelector('button');
    expect(button).not.toBeNull();
    expect(button.type).toBe('button');
    expect(button.getAttribute('aria-label')).toBe('What this field is for.');
    expect(button.tabIndex).toBe(0);
    expect(fixture.nativeElement.querySelector('spark-icon')).not.toBeNull();
  });

  it('does not let a click bubble to the header or label that contains it', () => {
    const fixture = create({ en: 'Help' });
    const outerClick = vi.fn();
    fixture.nativeElement.addEventListener('click', outerClick);

    const button: HTMLButtonElement = fixture.nativeElement.querySelector('button');
    const event = new MouseEvent('click', { bubbles: true, cancelable: true });
    button.dispatchEvent(event);

    expect(outerClick).not.toHaveBeenCalled();
    expect(event.defaultPrevented).toBe(true);
  });

  it('follows the current language, falling back to en', () => {
    const fixture = create({ en: 'Help', nl: 'Hulp' });
    expect(fixture.componentInstance.text()).toBe('Help');

    currentLanguage.set('nl');
    fixture.detectChanges();
    expect(fixture.componentInstance.text()).toBe('Hulp');
    expect(fixture.nativeElement.querySelector('button').getAttribute('aria-label')).toBe('Hulp');

    currentLanguage.set('fr');
    fixture.detectChanges();
    expect(fixture.componentInstance.text()).toBe('Help');
  });
});
