import { Component, OnInit, ChangeDetectionStrategy, inject, signal, afterNextRender, PLATFORM_ID, DestroyRef } from '@angular/core';
import { CommonModule, isPlatformBrowser } from '@angular/common';
import { RouterModule } from '@angular/router';
import { BsShellComponent, BsShellSidebarDirective, BsShellState } from '@mintplayer/ng-bootstrap/shell';
import { BsAccordionComponent, BsAccordionTabComponent, BsAccordionTabHeaderComponent } from '@mintplayer/ng-bootstrap/accordion';
import { BsNavbarTogglerComponent } from '@mintplayer/ng-bootstrap/navbar-toggler';
import { SparkService } from '../core/services/spark.service';
import { ProgramUnit, ProgramUnitGroup } from '../core/models';
import { ResolveTranslationPipe } from '../core/pipes/resolve-translation.pipe';
import { IconNamePipe } from '../core/pipes/icon-name.pipe';
import { RouterLinkPipe } from '../core/pipes/router-link.pipe';
import { IconComponent } from '../components/icon/icon.component';

@Component({
  selector: 'app-shell',
  imports: [CommonModule, RouterModule, BsShellComponent, BsShellSidebarDirective, BsAccordionComponent, BsAccordionTabComponent, BsAccordionTabHeaderComponent, BsNavbarTogglerComponent, IconComponent, ResolveTranslationPipe, IconNamePipe, RouterLinkPipe],
  templateUrl: './shell.component.html',
  styleUrl: './shell.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ShellComponent implements OnInit {
  private readonly sparkService = inject(SparkService);
  private readonly platformId = inject(PLATFORM_ID);
  private readonly destroyRef = inject(DestroyRef);

  programUnitGroups = signal<ProgramUnitGroup[]>([]);
  shellState = signal<BsShellState>('auto');
  isSidebarVisible = signal<boolean>(false);

  constructor() {
    afterNextRender(() => {
      this.setupResizeListener();
      this.updateSidebarVisibility();
    });
  }

  async ngOnInit(): Promise<void> {
    const config = await this.sparkService.getProgramUnits();
    this.programUnitGroups.set(config.programUnitGroups.sort((a, b) => a.order - b.order));
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

    const handler = () => this.updateSidebarVisibility();
    window.addEventListener('resize', handler);
    this.destroyRef.onDestroy(() => window.removeEventListener('resize', handler));
  }

  private updateSidebarVisibility(): void {
    const state = this.shellState();
    let isVisible: boolean;

    if (state === 'show') {
      isVisible = true;
    } else if (state === 'hide') {
      isVisible = false;
    } else {
      // 'auto' mode - check if above breakpoint
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
