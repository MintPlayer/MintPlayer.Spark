import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsButtonTypeDirective } from '@mintplayer/ng-bootstrap/button-type';
import { BsCardBodyComponent, BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { apiError, IconicApi } from '../../api/iconic-api';

/**
 * The link in a report mail (D20): an administrator opens it and confirms. Taking down only disables the share link;
 * the owner keeps the drawing. The server checks the administrator role; this page only asks.
 */
@Component({
  selector: 'app-takedown',
  imports: [RouterLink, BsAlertComponent, BsButtonTypeDirective, BsCardComponent, BsCardHeaderComponent, BsCardBodyComponent],
  template: `
    <div class="page">
      <bs-card>
        <bs-card-header>Take down a shared drawing</bs-card-header>
        <bs-card-body>
          @if (!token) {
            <bs-alert [color]="'warning'">This link has no drawing in it.</bs-alert>
          } @else if (done()) {
            <bs-alert [color]="'success'">"{{ done() }}" is no longer shared. Its owner still has it.</bs-alert>
            <a routerLink="/home">Home</a>
          } @else {
            <p>The share link <a [routerLink]="['/s', token]">/s/{{ token }}</a> stops working, for everyone. The owner keeps the drawing and is told when they open it.</p>
            @if (error()) { <bs-alert [color]="'danger'">{{ error() }}</bs-alert> }
            <button type="button" [color]="colors.danger" [disabled]="working()" (click)="takedown()">Take it down</button>
          }
        </bs-card-body>
      </bs-card>
    </div>
  `,
  styles: `.page { max-width: 40rem; margin: 0 auto; padding: 1rem; }`,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export default class TakedownComponent {
  private readonly api = inject(IconicApi);
  protected readonly colors = Color;
  protected readonly token = inject(ActivatedRoute).snapshot.queryParamMap.get('token');
  protected readonly working = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly done = signal<string | null>(null);

  async takedown(): Promise<void> {
    if (!this.token) return;
    this.working.set(true);
    this.error.set(null);
    try {
      const result = await this.api.takedown(this.token);
      this.done.set(result.name);
    } catch (e) {
      const err = apiError(e);
      this.error.set(err.status === 403 ? 'Only an administrator can take a drawing down.' : err.message);
    } finally {
      this.working.set(false);
    }
  }
}
