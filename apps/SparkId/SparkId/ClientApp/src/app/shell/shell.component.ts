import { Component, ChangeDetectionStrategy, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import {
  SparkShellComponent,
  SparkShellSidebarTopDirective,
  SparkShellTopbarEndDirective,
  SparkLanguageSelectorComponent,
} from '@mintplayer/ng-spark/shell';
import { SparkAuthBarComponent } from '@mintplayer/ng-spark/auth/auth-bar';
import { SparkAuthService } from '@mintplayer/ng-spark/auth/core';
import { SparkIdentityProviderTextPipe } from '@mintplayer/ng-spark/identity-provider';
import { SparkNotificationBellComponent } from '@mintplayer/ng-spark/notifications';

@Component({
  selector: 'app-shell',
  imports: [
    RouterOutlet, RouterLink, RouterLinkActive, SparkShellComponent, SparkShellTopbarEndDirective, SparkShellSidebarTopDirective,
    SparkLanguageSelectorComponent, SparkAuthBarComponent, SparkIdentityProviderTextPipe, SparkNotificationBellComponent,
  ],
  templateUrl: './shell.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ShellComponent {
  protected readonly auth = inject(SparkAuthService);
}
