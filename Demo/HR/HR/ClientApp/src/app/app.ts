import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { SparkRetryActionModalComponent } from '@mintplayer/ng-spark';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, SparkRetryActionModalComponent],
  templateUrl: './app.html',
  styleUrl: './app.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class App {
  protected readonly title = signal('ClientApp');
}
