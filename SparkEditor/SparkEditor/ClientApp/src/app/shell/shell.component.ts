import { Component, ChangeDetectionStrategy, inject, signal, effect, afterNextRender, PLATFORM_ID, DestroyRef } from '@angular/core';
import { CommonModule, isPlatformBrowser } from '@angular/common';
import { RouterModule } from '@angular/router';
import { BsShellComponent, BsShellSidebarDirective, BsShellState } from '@mintplayer/ng-bootstrap/shell';
import { BsAccordionComponent, BsAccordionTabComponent, BsAccordionTabHeaderComponent } from '@mintplayer/ng-bootstrap/accordion';
import { BsNavbarTogglerComponent } from '@mintplayer/ng-bootstrap/navbar-toggler';
import { BsSelectComponent, BsSelectOption } from '@mintplayer/ng-bootstrap/select';
import { FormsModule } from '@angular/forms';
import { KeyValuePipe } from '@angular/common';
import { SparkService, SparkLanguageService, SparkDataRefreshService, ProgramUnitGroup, SparkIconComponent, ResolveTranslationPipe, IconNamePipe, RouterLinkPipe } from '@mintplayer/ng-spark';
import { SparkFileWatchService } from '../services/spark-file-watch.service';

@Component({
  selector: 'app-shell',
  imports: [CommonModule, RouterModule, BsShellComponent, BsShellSidebarDirective, BsAccordionComponent, BsAccordionTabComponent, BsAccordionTabHeaderComponent, BsNavbarTogglerComponent, BsSelectComponent, BsSelectOption, SparkIconComponent, ResolveTranslationPipe, IconNamePipe, RouterLinkPipe, FormsModule, KeyValuePipe],
  templateUrl: './shell.component.html',
  styleUrl: './shell.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ShellComponent {
  private readonly sparkService = inject(SparkService);
  private readonly platformId = inject(PLATFORM_ID);
  private readonly destroyRef = inject(DestroyRef);
  private readonly fileWatchService = inject(SparkFileWatchService);
  private readonly refreshService = inject(SparkDataRefreshService);

  readonly lang = inject(SparkLanguageService);
  programUnitGroups = signal<ProgramUnitGroup[]>([]);
  shellState = signal<BsShellState>('auto');
  isSidebarVisible = signal<boolean>(false);

  constructor() {
    afterNextRender(() => {
      this.setupResizeListener();
      this.updateSidebarVisibility();

      // Connect to file watch WebSocket
      this.fileWatchService.connect();
    });

    this.destroyRef.onDestroy(() => this.fileWatchService.disconnect());

    // When external file changes arrive, refresh the sidebar and notify Spark components
    effect(() => {
      const event = this.fileWatchService.fileChanged();
      if (event) {
        if (event.affectedEntities.includes('ProgramUnitGroupDef')) {
          this.loadProgramUnits();
        }
        // Notify all Spark components to re-fetch their data
        this.refreshService.refresh(event.affectedEntities);
      }
    });

    this.loadProgramUnits();
  }

  private async loadProgramUnits(): Promise<void> {
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
