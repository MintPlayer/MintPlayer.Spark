import { Component, ChangeDetectionStrategy } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { SparkShellComponent, SparkShellTabDirective } from '@mintplayer/ng-spark/shell';
import { SparkIconComponent } from '@mintplayer/ng-spark/icon';

@Component({
  selector: 'app-shell',
  imports: [
    RouterOutlet, RouterLink, RouterLinkActive,
    SparkShellComponent, SparkShellTabDirective,
    SparkIconComponent,
  ],
  templateUrl: './shell.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ShellComponent {}
