import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { SparkToastContainerComponent } from '@mintplayer/ng-spark/client-operations';
import { SparkRetryActionModalComponent } from '@mintplayer/ng-spark/retry-action-modal';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, SparkRetryActionModalComponent, SparkToastContainerComponent],
  templateUrl: './app.html',
  styleUrl: './app.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class App {}
