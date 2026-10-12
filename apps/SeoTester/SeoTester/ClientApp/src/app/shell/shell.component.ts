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
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ShellComponent {
  protected readonly auth = inject(SparkAuthService);
}
