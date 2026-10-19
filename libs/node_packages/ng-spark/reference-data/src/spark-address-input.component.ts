import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  forwardRef,
  inject,
  input,
  model,
  signal,
} from '@angular/core';
import { takeUntilDestroyed, toObservable, toSignal } from '@angular/core/rxjs-interop';
import { ControlValueAccessor, NG_VALUE_ACCESSOR } from '@angular/forms';
import { Subject, debounceTime, distinctUntilChanged, map, of, switchMap } from 'rxjs';
import { CountryProfileResponse, StreetSuggestItem, SuggestItem } from './generated/contracts';
import { ReferenceDataApi } from './reference-data-api.service';
import { SparkAddress, countryChoices, isEmptyAddress, renderAddressLines } from './address';

let nextId = 0;

/** The viewer's local calendar date, `yyyy-MM-dd` (the as-of date of suggestions; no zone arithmetic, K10). */
function localDate(): string {
  const now = new Date();
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
}

/**
 * `<spark-address-input>` (PRD §4.15): a country picker (`Intl.DisplayNames` + `Intl.Collator`), then typeaheads that
 * narrow per the country profile's steps (postal locality → street), then the house number and box. Typing is
 * debounced and a newer query cancels the older one (`switchMap`). Free text is always allowed: "not listed" opens
 * plain fields, and with no instance configured (free-text mode) only plain fields are shown. The value is a flat
 * {@link SparkAddress}; `Verified` says whether the ids came from reference data.
 *
 * Usable as a form control (`ControlValueAccessor`) or with `[(value)]`.
 */
@Component({
  selector: 'spark-address-input',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './spark-address-input.component.html',
  styleUrl: './spark-address-input.component.scss',
  providers: [{ provide: NG_VALUE_ACCESSOR, useExisting: forwardRef(() => SparkAddressInputComponent), multi: true }],
})
export class SparkAddressInputComponent implements ControlValueAccessor {
  private readonly api = inject(ReferenceDataApi);
  private readonly destroyRef = inject(DestroyRef);

  /** The address. */
  readonly value = model<SparkAddress | null>(null);
  /** The UI language (`lang` of every request, and the country names). Defaults to the document's language. */
  readonly lang = input<string>(typeof document !== 'undefined' ? document.documentElement.lang || 'en' : 'en');
  /** Countries listed first in the picker. */
  readonly preferredCountries = input<readonly string[]>(['BE', 'NL', 'FR', 'LU', 'DE']);
  /** The country a new address starts in. */
  readonly defaultCountry = input<string>('BE');
  /** Disabled by the form. */
  readonly disabled = signal(false);

  protected readonly id = `spark-address-${nextId++}`;
  protected readonly enabled = this.api.enabled;
  protected readonly countries = computed(() => countryChoices(this.lang(), this.preferredCountries()));
  protected readonly country = computed(() => (this.value()?.CountryCode ?? this.defaultCountry()).toUpperCase());
  /** Plain fields: free-text mode, or the viewer chose "not listed". */
  protected readonly freeText = signal(!this.api.enabled);

  private readonly profile = toSignal(
    toObservable(this.country).pipe(switchMap(cc => this.enabled ? this.api.profile(cc) : of(null))),
    { initialValue: null as CountryProfileResponse | null });

  protected readonly lines = computed(() => renderAddressLines(this.value(), this.profile()?.addressLines));
  protected readonly hasBox = computed(() => !!this.profile()?.boxPattern || !this.profile());

  // ---- the locality typeahead ----
  protected readonly localityText = signal('');
  protected readonly localityOpen = signal(false);
  protected readonly localityActive = signal(-1);
  private readonly localityQuery = new Subject<string>();
  protected readonly localities = signal<SuggestItem[]>([]);

  // ---- the street typeahead ----
  protected readonly streetText = signal('');
  protected readonly streetOpen = signal(false);
  protected readonly streetActive = signal(-1);
  private readonly streetQuery = new Subject<string>();
  protected readonly streets = signal<StreetSuggestItem[]>([]);

  private onChange: (value: SparkAddress | null) => void = () => {};
  private onTouched: () => void = () => {};

  constructor() {
    this.localityQuery.pipe(
      map(q => q.trim()),
      debounceTime(200),
      distinctUntilChanged(),
      switchMap(q => q.length < 2 ? of(null) : this.api.suggest(q, { country: this.country().toLowerCase(), lang: this.lang(), asOf: localDate(), limit: 10 })),
      takeUntilDestroyed(this.destroyRef),
    ).subscribe(response => {
      this.localities.set(response?.items ?? []);
      this.localityActive.set(-1);
      this.localityOpen.set((response?.items.length ?? 0) > 0);
    });

    this.streetQuery.pipe(
      map(q => q.trim()),
      debounceTime(200),
      distinctUntilChanged(),
      switchMap(q => {
        const v = this.value();
        if (q.length < 2 || (!v?.MunicipalityId && !v?.PostalCode)) return of(null);
        return this.api.streets(q, { municipality: v?.MunicipalityId, postalCode: v?.PostalCode, lang: this.lang(), limit: 10 });
      }),
      takeUntilDestroyed(this.destroyRef),
    ).subscribe(response => {
      this.streets.set(response?.items ?? []);
      this.streetActive.set(-1);
      this.streetOpen.set((response?.items.length ?? 0) > 0);
    });
  }

  // ---- ControlValueAccessor ----

  writeValue(value: SparkAddress | null): void {
    this.value.set(value ?? null);
    this.localityText.set(value?.PostalCode || value?.LocalityName ? `${value?.PostalCode ?? ''} ${value?.LocalityName ?? ''}`.trim() : '');
    this.streetText.set(value?.StreetName ?? '');
  }

  registerOnChange(fn: (value: SparkAddress | null) => void): void {
    this.onChange = fn;
  }

  registerOnTouched(fn: () => void): void {
    this.onTouched = fn;
  }

  setDisabledState(isDisabled: boolean): void {
    this.disabled.set(isDisabled);
  }

  // ---- edits ----

  protected setCountry(code: string): void {
    // Another country invalidates every id picked in the previous one.
    this.patch({
      CountryCode: code, PostalCodeId: null, PostalCode: null, LocalityKey: null, LocalityName: null, LocalityPlaceId: null,
      MunicipalityId: null, MunicipalityName: null, StreetId: null, StreetName: null, TimeZone: null, Verified: false,
    });
    this.localityText.set('');
    this.streetText.set('');
  }

  protected onLocalityInput(text: string): void {
    this.localityText.set(text);
    this.localityQuery.next(text);
  }

  protected pickLocality(item: SuggestItem): void {
    this.patch({
      CountryCode: item.country.toUpperCase(),
      PostalCodeId: item.postalCode?.id ?? null,
      PostalCode: item.postalCode?.code ?? null,
      LocalityKey: item.locality?.key ?? null,
      LocalityName: item.locality?.name ?? item.label,
      LocalityPlaceId: item.locality?.place ?? item.place?.id ?? null,
      MunicipalityId: item.municipality?.id ?? null,
      MunicipalityName: item.municipality?.name ?? null,
      TimeZone: item.timeZone ?? null,
      Release: null,
      Verified: !!item.postalCode,
      StreetId: null,
      StreetName: null,
    });
    this.localityText.set(item.label);
    this.localityOpen.set(false);
  }

  protected onStreetInput(text: string): void {
    this.streetText.set(text);
    // Typing changes the street: it is free text until a suggestion is picked.
    this.patch({ StreetName: text, StreetId: null });
    this.streetQuery.next(text);
  }

  protected pickStreet(item: StreetSuggestItem): void {
    this.patch({ StreetId: item.id, StreetName: item.label });
    this.streetText.set(item.label);
    this.streetOpen.set(false);
  }

  protected setField(field: keyof SparkAddress, text: string): void {
    this.patch({ [field]: text } as Partial<SparkAddress>);
  }

  protected toggleFreeText(on: boolean): void {
    this.freeText.set(on || !this.enabled);
    if (on) this.patch({ Verified: false, PostalCodeId: null, StreetId: null, LocalityKey: null, LocalityPlaceId: null });
  }

  protected onKey(event: KeyboardEvent, list: 'locality' | 'street'): void {
    const items = list === 'locality' ? this.localities() : this.streets();
    const active = list === 'locality' ? this.localityActive : this.streetActive;
    const open = list === 'locality' ? this.localityOpen : this.streetOpen;
    if (!open() || items.length === 0) return;
    if (event.key === 'ArrowDown') {
      active.set((active() + 1) % items.length);
      event.preventDefault();
    } else if (event.key === 'ArrowUp') {
      active.set((active() - 1 + items.length) % items.length);
      event.preventDefault();
    } else if (event.key === 'Enter' && active() >= 0) {
      if (list === 'locality') this.pickLocality(this.localities()[active()]);
      else this.pickStreet(this.streets()[active()]);
      event.preventDefault();
    } else if (event.key === 'Escape') {
      open.set(false);
    }
  }

  protected close(list: 'locality' | 'street'): void {
    // Later than the click on an option, which would otherwise never land.
    setTimeout(() => (list === 'locality' ? this.localityOpen : this.streetOpen).set(false), 150);
    this.onTouched();
  }

  private patch(change: Partial<SparkAddress>): void {
    const next: SparkAddress = { ...(this.value() ?? { CountryCode: this.country() }), ...change };
    next.Lines = renderAddressLines(next, this.profile()?.addressLines);
    const value = isEmptyAddress(next) ? null : next;
    this.value.set(value);
    this.onChange(value);
  }
}
