import { Component, signal } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { RetryActionModalComponent } from './components/retry-action-modal/retry-action-modal.component';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RetryActionModalComponent],
  templateUrl: './app.html',
  styleUrl: './app.scss'
})
export class App {
  protected readonly title = signal('ClientApp');
}
