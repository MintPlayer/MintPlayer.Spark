import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterLink, RouterOutlet } from '@angular/router';
import { SparkShellComponent, SparkShellTopbarEndDirective, SparkLanguageSelectorComponent } from '@mintplayer/ng-spark/shell';
import { SparkAuthBarComponent } from '@mintplayer/ng-spark/auth/auth-bar';
import { SparkAuthService } from '@mintplayer/ng-spark/auth/core';
import { SparkNotificationBellComponent } from '@mintplayer/ng-spark/notifications';
import { SparkOrganizationSwitcherComponent } from '@mintplayer/ng-spark/organizations';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';

@Component({
  selector: 'app-shell',
  imports: [
    RouterLink, RouterOutlet, SparkShellComponent, SparkShellTopbarEndDirective, SparkLanguageSelectorComponent,
    SparkAuthBarComponent, SparkNotificationBellComponent, SparkOrganizationSwitcherComponent, TranslateKeyPipe,
  ],
  templateUrl: './shell.component.html',
  // The connect-agent link is drawn like <spark-auth-bar>'s login button: an outline in the topbar's own text colour.
  styles: [`
    .topbar-btn {
      --bs-btn-color: currentColor;
      --bs-btn-border-color: currentColor;
      --bs-btn-hover-color: currentColor;
      --bs-btn-hover-bg: color-mix(in srgb, currentColor 15%, transparent);
      --bs-btn-hover-border-color: currentColor;
      --bs-btn-active-color: currentColor;
      --bs-btn-active-bg: color-mix(in srgb, currentColor 25%, transparent);
      --bs-btn-active-border-color: currentColor;
    }
  `],
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ShellComponent {
  protected readonly auth = inject(SparkAuthService);
}
