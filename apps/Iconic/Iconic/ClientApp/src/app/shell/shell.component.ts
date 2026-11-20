import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { SparkAuthBarComponent } from '@mintplayer/ng-spark/auth/auth-bar';
import { SparkLanguageSelectorComponent, SparkShellComponent, SparkShellTopbarEndDirective } from '@mintplayer/ng-spark/shell';

@Component({
  selector: 'app-shell',
  imports: [RouterOutlet, SparkShellComponent, SparkShellTopbarEndDirective, SparkLanguageSelectorComponent, SparkAuthBarComponent],
  templateUrl: './shell.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ShellComponent {}
