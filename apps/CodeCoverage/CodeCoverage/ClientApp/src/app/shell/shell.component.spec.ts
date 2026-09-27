import { Component, Directive, inject, input, output, provideZonelessChangeDetection, signal, TemplateRef, ViewContainerRef } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { FormsModule } from '@angular/forms';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { describe, expect, it, vi } from 'vitest';
import { BsSelectComponent, BsSelectOption } from '@mintplayer/ng-bootstrap/select';
import { SparkShellComponent, SparkShellMainHeaderDirective, SparkShellTopbarEndDirective } from '@mintplayer/ng-spark/shell';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { SparkAuthBarComponent } from '@mintplayer/ng-spark-auth/auth-bar';
import { settle } from '../../testing/test-utils';
import { ShellComponent } from './shell.component';

/*
 * The real frame is the `mp-shell` Lit element and the select is `mp-select`; jsdom hosts
 * neither. These stand-ins keep the selectors and bindings, and the slot directives simply
 * render their template in place, so what is tested is this component's own template.
 */

@Component({ selector: 'spark-shell', template: '<ng-content />' })
class StubSparkShell {
  readonly title = input<string>();
  readonly breakpoint = input<string>();
  readonly sidebarTheme = input<string>();
}

@Directive({ selector: '[sparkShellTopbarEnd]' })
class StubTopbarEnd {
  constructor() { inject(ViewContainerRef).createEmbeddedView(inject(TemplateRef)); }
}

@Directive({ selector: '[sparkShellMainHeader]' })
class StubMainHeader {
  constructor() { inject(ViewContainerRef).createEmbeddedView(inject(TemplateRef)); }
}

@Component({ selector: 'bs-select', template: '<ng-content />' })
class StubSelect {
  readonly ngModel = input<unknown>();
  readonly ngModelChange = output<unknown>();
}

@Directive({ selector: 'option' })
class StubOption {
  readonly ngValue = input<unknown>();
}

@Component({ selector: 'spark-auth-bar', template: '' })
class StubAuthBar {}

describe('ShellComponent', () => {
  async function setup(languages: Record<string, Record<string, string>>) {
    const lang = {
      languages: signal(languages),
      language: signal('en'),
      setLanguage: vi.fn(),
      t: (key: string) => key,
    };
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideNoopAnimations(),
        provideRouter([]),
        { provide: SparkLanguageService, useValue: lang },
      ],
    });
    TestBed.overrideComponent(ShellComponent, {
      remove: {
        imports: [FormsModule, SparkShellComponent, SparkShellTopbarEndDirective, SparkShellMainHeaderDirective,
          BsSelectComponent, BsSelectOption, SparkAuthBarComponent],
      },
      add: { imports: [StubSparkShell, StubTopbarEnd, StubMainHeader, StubSelect, StubOption, StubAuthBar] },
    });
    const fixture = TestBed.createComponent(ShellComponent);
    await settle(fixture);
    return { fixture, lang };
  }

  function el(fixture: ComponentFixture<unknown>): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  it('titles the frame with the translated app title and always mounts the auth bar', async () => {
    const { fixture } = await setup({ en: { en: 'English' } });

    expect(fixture.debugElement.query(By.directive(StubSparkShell)).componentInstance.title()).toBe('app.title');
    expect(el(fixture).querySelector('spark-auth-bar')).not.toBeNull();
    expect(el(fixture).querySelector('router-outlet')).not.toBeNull();
  });

  // Replacing the topbar slot drops Spark's own selector, so the shell must redraw it — but only
  // when there is a choice to make.
  it('hides the language selector when there is only one language', async () => {
    const { fixture } = await setup({ en: { en: 'English' } });

    expect(el(fixture).querySelector('bs-select')).toBeNull();
  });

  it('offers one option per language, labelled in the current language, and switches on change', async () => {
    const { fixture, lang } = await setup({ en: { en: 'English', nl: 'Engels' }, nl: { en: 'Dutch', nl: 'Nederlands' } });

    const select = fixture.debugElement.query(By.directive(StubSelect));
    expect(select.componentInstance.ngModel()).toBe('en');
    const options = Array.from(el(fixture).querySelectorAll('option'));
    expect(options.map(o => o.textContent!.trim())).toEqual(['English', 'Dutch']);
    expect(fixture.debugElement.queryAll(By.directive(StubOption)).map(o => o.injector.get(StubOption).ngValue())).toEqual(['en', 'nl']);

    (select.componentInstance as StubSelect).ngModelChange.emit('nl');
    expect(lang.setLanguage).toHaveBeenCalledWith('nl');
  });

  it('shows a login error in the main header, and clears it when the alert is dismissed', async () => {
    const { fixture } = await setup({ en: { en: 'English' } });
    expect(el(fixture).querySelector('bs-alert')).toBeNull();

    fixture.componentInstance.loginError.set('GitHub sign-in failed');
    await settle(fixture);
    expect(el(fixture).querySelector('bs-alert')!.textContent).toContain('GitHub sign-in failed');

    (el(fixture).querySelector('bs-alert-close button') as HTMLButtonElement).click();
    await settle(fixture);

    expect(fixture.componentInstance.loginError()).toBeNull();
    expect(el(fixture).querySelector('bs-alert')).toBeNull();
  });

  it('ignores the alert becoming visible', async () => {
    const { fixture } = await setup({ en: { en: 'English' } });
    fixture.componentInstance.loginError.set('boom');

    fixture.componentInstance.onLoginAlertVisible(true);

    expect(fixture.componentInstance.loginError()).toBe('boom');
  });
});
