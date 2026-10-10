import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { SparkShellComponent, SparkShellTopbarEndDirective, SparkLanguageSelectorComponent } from '@mintplayer/ng-spark/shell';
import { SparkAuthBarComponent } from '@mintplayer/ng-spark/auth/auth-bar';
import { SparkAuthService } from '@mintplayer/ng-spark/auth/core';
import { SparkNotificationBellComponent } from '@mintplayer/ng-spark/notifications';

@Component({
  selector: 'app-shell',
  imports: [
    RouterOutlet, SparkShellComponent, SparkShellTopbarEndDirective, SparkLanguageSelectorComponent,
    SparkAuthBarComponent, SparkNotificationBellComponent,
  ],
  templateUrl: './shell.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ShellComponent {
  protected readonly auth = inject(SparkAuthService);
}
