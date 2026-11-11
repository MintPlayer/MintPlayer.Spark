import { Component, Directive, inject, input, provideZonelessChangeDetection, signal, TemplateRef, ViewContainerRef } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { describe, expect, it } from 'vitest';
import {
  SparkLanguageSelectorComponent, SparkShellComponent, SparkShellMainHeaderDirective, SparkShellTopbarEndDirective,
} from '@mintplayer/ng-spark/shell';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { SparkAuthBarComponent } from '@mintplayer/ng-spark/auth/auth-bar';
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
}

@Directive({ selector: '[sparkShellTopbarEnd]' })
class StubTopbarEnd {
  constructor() { inject(ViewContainerRef).createEmbeddedView(inject(TemplateRef)); }
}

@Directive({ selector: '[sparkShellMainHeader]' })
class StubMainHeader {
  constructor() { inject(ViewContainerRef).createEmbeddedView(inject(TemplateRef)); }
}

@Component({ selector: 'spark-language-selector', template: '' })
class StubLanguageSelector {}

@Component({ selector: 'spark-auth-bar', template: '' })
class StubAuthBar {}

describe('ShellComponent', () => {
  async function setup() {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideNoopAnimations(),
        provideRouter([]),
        { provide: SparkLanguageService, useValue: { language: signal('en'), t: (key: string) => key } },
      ],
    });
    TestBed.overrideComponent(ShellComponent, {
      remove: {
        imports: [SparkShellComponent, SparkShellTopbarEndDirective, SparkShellMainHeaderDirective,
          SparkLanguageSelectorComponent, SparkAuthBarComponent],
      },
      add: { imports: [StubSparkShell, StubTopbarEnd, StubMainHeader, StubLanguageSelector, StubAuthBar] },
    });
    const fixture = TestBed.createComponent(ShellComponent);
    await settle(fixture);
    return { fixture };
  }

  function el(fixture: ComponentFixture<unknown>): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  it('titles the frame with the translated app title and always mounts the auth bar', async () => {
    const { fixture } = await setup();

    expect(fixture.debugElement.query(By.directive(StubSparkShell)).componentInstance.title()).toBe('app.title');
    expect(el(fixture).querySelector('spark-auth-bar')).not.toBeNull();
    expect(el(fixture).querySelector('router-outlet')).not.toBeNull();
  });

  // Replacing the topbar slot drops Spark's own selector, so the shell must place it again.
  it("keeps Spark's language selector in the replaced topbar slot", async () => {
    const { fixture } = await setup();

    expect(el(fixture).querySelector('spark-language-selector')).not.toBeNull();
  });

  it('shows a login error in the main header, and clears it when the alert is dismissed', async () => {
    const { fixture } = await setup();
    expect(el(fixture).querySelector('bs-alert')).toBeNull();

    fixture.componentInstance.loginError.set('GitHub sign-in failed');
    await settle(fixture);
    expect(el(fixture).querySelector('bs-alert')!.textContent).toContain('GitHub sign-in failed');

    (el(fixture).querySelector('bs-alert button.btn-close') as HTMLButtonElement).click();
    await settle(fixture);

    expect(fixture.componentInstance.loginError()).toBeNull();
    expect(el(fixture).querySelector('bs-alert')).toBeNull();
  });

  it('ignores the alert becoming visible', async () => {
    const { fixture } = await setup();
    fixture.componentInstance.loginError.set('boom');

    fixture.componentInstance.onLoginAlertVisible(true);

    expect(fixture.componentInstance.loginError()).toBe('boom');
  });
});
