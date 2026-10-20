import { Component, ChangeDetectionStrategy } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { SparkShellComponent, SparkShellTopbarEndDirective, SparkLanguageSelectorComponent } from '@mintplayer/ng-spark/shell';
import { SparkAuthBarComponent } from '@mintplayer/ng-spark/auth/auth-bar';
import { SparkPendingChangesLinkComponent } from '@mintplayer/ng-spark/replication';

@Component({
  selector: 'app-shell',
  imports: [RouterOutlet, SparkShellComponent, SparkShellTopbarEndDirective, SparkLanguageSelectorComponent, SparkAuthBarComponent, SparkPendingChangesLinkComponent],
  templateUrl: './shell.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ShellComponent {}
