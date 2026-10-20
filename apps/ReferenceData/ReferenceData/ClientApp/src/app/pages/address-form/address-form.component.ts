import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { BsFormComponent } from '@mintplayer/ng-bootstrap/form';
import { SparkDateTimeComponent } from '@mintplayer/ng-spark/date-time';
import { REFERENCE_DATA_CONFIG, SourcesResponse, SparkAddress, SparkAddressInputComponent } from '@mintplayer/ng-spark/reference-data';
import { catchError, of } from 'rxjs';

/**
 * The consumer page (docs/reference_data_PRD.md §4.16): the address form against this instance, a free-text variant
 * with no instance at all, the zone label of the picked address through the D36 provider, and the release and
 * sources this instance serves.
 */
@Component({
  selector: 'app-address-form',
  imports: [BsFormComponent, SparkAddressInputComponent, SparkDateTimeComponent],
  templateUrl: './address-form.component.html',
  styleUrl: './address-form.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export default class AddressFormComponent {
  private readonly http = inject(HttpClient);
  protected readonly baseUrl = inject(REFERENCE_DATA_CONFIG).baseUrl;

  protected readonly address = signal<SparkAddress | null>(null);
  protected readonly now = new Date().toISOString();

  protected readonly sources = toSignal(
    this.http.get<SourcesResponse>('/api/v1/sources').pipe(catchError(() => of(null))),
    { initialValue: null });

  protected readonly json = computed(() => JSON.stringify(this.address(), null, 2));
}
