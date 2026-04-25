import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { SparkRetryActionModalComponent } from '@mintplayer/ng-spark/retry-action-modal';
import { SparkToastContainerComponent } from '@mintplayer/ng-spark/client-instructions';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, SparkRetryActionModalComponent, SparkToastContainerComponent],
  templateUrl: './app.html',
  styleUrl: './app.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class App {
  protected readonly title = signal('ClientApp');
}
