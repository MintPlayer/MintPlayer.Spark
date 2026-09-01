import { Component, ChangeDetectionStrategy, inject } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import {
  SparkShellComponent,
  SparkShellSidebarHeaderDirective,
  SparkShellSidebarTopDirective,
  SparkShellTopbarEndDirective,
  SparkLanguageSelectorComponent,
} from '@mintplayer/ng-spark/shell';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkAuthService } from '@mintplayer/ng-spark-auth/core';

@Component({
  selector: 'app-shell',
  imports: [
    RouterOutlet, RouterLink, RouterLinkActive,
    SparkShellComponent, SparkShellSidebarHeaderDirective, SparkShellSidebarTopDirective,
    SparkShellTopbarEndDirective, SparkLanguageSelectorComponent,
    TranslateKeyPipe,
  ],
  templateUrl: './shell.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ShellComponent {
  private readonly router = inject(Router);
  readonly authService = inject(SparkAuthService);

  async logout() {
    await this.authService.logout();
    await this.router.navigateByUrl('/');
  }
}
