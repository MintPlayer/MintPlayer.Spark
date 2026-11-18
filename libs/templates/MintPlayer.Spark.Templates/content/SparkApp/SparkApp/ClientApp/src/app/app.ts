import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { SparkRetryActionModalComponent } from '@mintplayer/ng-spark/retry-action-modal';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, SparkRetryActionModalComponent],
  template: `
    <router-outlet />
    <spark-retry-action-modal />
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class App {}
