import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { RetryActionModalComponent } from './components/retry-action-modal/retry-action-modal.component';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RetryActionModalComponent],
  templateUrl: './app.html',
  styleUrl: './app.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class App {
  protected readonly title = signal('ClientApp');
}
