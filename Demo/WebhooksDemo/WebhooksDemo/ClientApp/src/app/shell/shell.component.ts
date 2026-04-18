import { Component, ChangeDetectionStrategy, inject, signal, effect, afterNextRender, PLATFORM_ID, DestroyRef, NgZone } from '@angular/core';
import { CommonModule, isPlatformBrowser } from '@angular/common';
import { Router, RouterModule } from '@angular/router';
import { BsShellComponent, BsShellSidebarDirective, BsShellState } from '@mintplayer/ng-bootstrap/shell';
import { BsAccordionComponent, BsAccordionTabComponent, BsAccordionTabHeaderComponent } from '@mintplayer/ng-bootstrap/accordion';
import { BsNavbarTogglerComponent } from '@mintplayer/ng-bootstrap/navbar-toggler';
import { BsSelectComponent, BsSelectOption } from '@mintplayer/ng-bootstrap/select';
import { SparkService, SparkLanguageService } from '@mintplayer/ng-spark/services';
import { SparkIconComponent } from '@mintplayer/ng-spark/icon';
import { ResolveTranslationPipe, TranslateKeyPipe, IconNamePipe, RouterLinkPipe } from '@mintplayer/ng-spark/pipes';
import { ProgramUnitGroup } from '@mintplayer/ng-spark/models';
import { SparkAuthService } from '@mintplayer/ng-spark-auth/core';
import { FormsModule } from '@angular/forms';
import { KeyValuePipe } from '@angular/common';

@Component({
  selector: 'app-shell',
  imports: [CommonModule, RouterModule, BsShellComponent, BsShellSidebarDirective, BsAccordionComponent, BsAccordionTabComponent, BsAccordionTabHeaderComponent, BsNavbarTogglerComponent, BsSelectComponent, BsSelectOption, SparkIconComponent, ResolveTranslationPipe, TranslateKeyPipe, IconNamePipe, RouterLinkPipe, FormsModule, KeyValuePipe],
  templateUrl: './shell.component.html',
  styleUrl: './shell.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ShellComponent {
  private readonly sparkService = inject(SparkService);
  private readonly platformId = inject(PLATFORM_ID);
  private readonly destroyRef = inject(DestroyRef);
  private readonly router = inject(Router);
  private readonly zone = inject(NgZone);
  readonly authService = inject(SparkAuthService);

  readonly lang = inject(SparkLanguageService);
  programUnitGroups = signal<ProgramUnitGroup[]>([]);
  shellState = signal<BsShellState>('auto');
  isSidebarVisible = signal<boolean>(false);

  constructor() {
    afterNextRender(() => {
      this.setupResizeListener();
      this.updateSidebarVisibility();
    });

    effect(() => {
      this.authService.user();
      this.loadProgramUnits();
    });
  }

  private async loadProgramUnits(): Promise<void> {
    const config = await this.sparkService.getProgramUnits();
    this.programUnitGroups.set(config.programUnitGroups.sort((a, b) => a.order - b.order));
  }

  loginWithGitHub(): void {
    const url = '/spark/auth/external-login?provider=GitHub&returnUrl=/github-projects';
    const popup = window.open(url, 'github-login', 'width=600,height=700');

    const onMessage = (event: MessageEvent) => {
      if (event.origin !== window.location.origin) return;
      if (event.data?.type !== 'external-login-success') return;

      window.removeEventListener('message', onMessage);
      popup?.close();

      this.zone.run(async () => {
        await this.authService.checkAuth();
        this.router.navigate(['/github-projects']);
      });
    };

    window.addEventListener('message', onMessage);
  }

  async logout(): Promise<void> {
    await this.authService.logout();
  }

  toggleSidebar(open: boolean) {
    this.shellState.set(open ? 'show' : 'hide');
    this.updateSidebarVisibility();
  }

  onMenuItemClick() {
    if (this.shellState() !== 'auto') {
      this.shellState.set('hide');
      this.updateSidebarVisibility();
    }
  }

  private setupResizeListener(): void {
    if (!isPlatformBrowser(this.platformId)) {
      return;
    }

    const onResize = () => this.updateSidebarVisibility();
    window.addEventListener('resize', onResize);
    this.destroyRef.onDestroy(() => window.removeEventListener('resize', onResize));
  }

  private updateSidebarVisibility(): void {
    const state = this.shellState();
    let isVisible: boolean;

    if (state === 'show') {
      isVisible = true;
    } else if (state === 'hide') {
      isVisible = false;
    } else {
      isVisible = this.isAboveBreakpoint();
    }

    this.isSidebarVisible.set(isVisible);
  }

  private isAboveBreakpoint(): boolean {
    if (!isPlatformBrowser(this.platformId)) {
      return false;
    }
    // Bootstrap 'md' breakpoint is 768px
    return window.innerWidth >= 768;
  }

}
