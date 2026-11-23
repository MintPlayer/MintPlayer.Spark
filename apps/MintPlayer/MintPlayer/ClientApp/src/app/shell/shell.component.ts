import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { SparkShellComponent, SparkShellTopbarEndDirective, SparkLanguageSelectorComponent } from '@mintplayer/ng-spark/shell';
import { SparkAuthBarComponent } from '@mintplayer/ng-spark/auth/auth-bar';
import { SparkSiteSearchComponent } from '@mintplayer/ng-spark/search';
import { SparkNotificationBellComponent } from '@mintplayer/ng-spark/notifications';
import { PlayerDockComponent } from '../player/player-dock.component';
import { QueueTogglerComponent } from '../player/queue-toggler.component';

@Component({
  selector: 'app-shell',
  imports: [RouterOutlet, SparkShellComponent, SparkShellTopbarEndDirective, SparkLanguageSelectorComponent, SparkSiteSearchComponent, SparkAuthBarComponent, SparkNotificationBellComponent, PlayerDockComponent, QueueTogglerComponent],
  templateUrl: './shell.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ShellComponent {}
