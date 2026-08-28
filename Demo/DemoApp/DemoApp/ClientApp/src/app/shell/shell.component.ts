import { Component, ChangeDetectionStrategy } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { BsAccordionComponent, BsAccordionTabComponent, BsAccordionTabHeaderDirective } from '@mintplayer/ng-bootstrap/accordion';
import { SparkShellComponent, SparkShellSidebarTabsDirective } from '@mintplayer/ng-spark/shell';
import { SparkIconComponent } from '@mintplayer/ng-spark/icon';

@Component({
  selector: 'app-shell',
  imports: [
    RouterOutlet, RouterLink, RouterLinkActive,
    SparkShellComponent, SparkShellSidebarTabsDirective,
    BsAccordionComponent, BsAccordionTabComponent, BsAccordionTabHeaderDirective,
    SparkIconComponent,
  ],
  templateUrl: './shell.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ShellComponent {}
