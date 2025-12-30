import { Component, OnInit, ChangeDetectionStrategy, ChangeDetectorRef, inject, signal, afterNextRender, PLATFORM_ID, DestroyRef } from '@angular/core';
import { CommonModule, isPlatformBrowser } from '@angular/common';
import { RouterModule } from '@angular/router';
import { BsShellModule, BsShellState } from '@mintplayer/ng-bootstrap/shell';
import { BsAccordionModule } from '@mintplayer/ng-bootstrap/accordion';
import { BsNavbarTogglerComponent } from '@mintplayer/ng-bootstrap/navbar-toggler';
import { SparkService } from '../core/services/spark.service';
import { ProgramUnitGroup } from '../core/models';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { fromEvent } from 'rxjs';

@Component({
  selector: 'app-shell',
  imports: [CommonModule, RouterModule, BsShellModule, BsAccordionModule, BsNavbarTogglerComponent],
  templateUrl: './shell.component.html',
  styleUrl: './shell.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ShellComponent implements OnInit {
  private readonly sparkService = inject(SparkService);
  private readonly cdr = inject(ChangeDetectorRef);
  private readonly platformId = inject(PLATFORM_ID);
  private readonly destroyRef = inject(DestroyRef);

  programUnitGroups: ProgramUnitGroup[] = [];
  shellState = signal<BsShellState>('auto');
  isSidebarVisible = signal<boolean>(false);

  constructor() {
    afterNextRender(() => {
      this.setupResizeListener();
      this.updateSidebarVisibility();
    });
  }

  ngOnInit(): void {
    this.sparkService.getProgramUnits().subscribe(config => {
      this.programUnitGroups = config.programUnitGroups.sort((a, b) => a.order - b.order);
      this.cdr.markForCheck();
    });
  }

  getRouterLink(unit: any): string[] {
    if (unit.type === 'query') {
      return ['/query', unit.queryId];
    } else if (unit.type === 'persistentObject') {
      return ['/po', unit.persistentObjectId];
    }
    return ['/'];
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

    fromEvent(window, 'resize')
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => {
        this.updateSidebarVisibility();
      });
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
    this.cdr.markForCheck();
  }

  private isAboveBreakpoint(): boolean {
    if (!isPlatformBrowser(this.platformId)) {
      return false;
    }
    // Bootstrap 'md' breakpoint is 768px
    return window.innerWidth >= 768;
  }
}
