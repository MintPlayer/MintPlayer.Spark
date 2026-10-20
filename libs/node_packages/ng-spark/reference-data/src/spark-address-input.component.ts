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
import { BsTypeaheadComponent } from '@mintplayer/ng-bootstrap/typeahead';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { Subject, map, of, switchMap } from 'rxjs';
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
 * One option of a `bs-typeahead` box. ng-bootstrap 22.25 renders `text` only (no item template yet, RX5), so the
 * secondary line and the "ended" hint are folded into it; `item` is what a pick applies.
 */
export interface AddressSuggestion<T> {
  text: string;
  item: T;
}

/**
 * `<spark-address-input>` (PRD §4.15): a country picker (`Intl.DisplayNames` + `Intl.Collator`), then `bs-typeahead`
 * boxes that narrow per the country profile's steps (postal locality → street), then the house number and box. The
 * service layer debounces typing and a newer query cancels the older one ({@link ReferenceDataApi.typeahead}). Free
 * text is always allowed: what is typed in the street box stays as the street until a suggestion is picked, "not
 * listed" opens plain fields, and with no instance configured (free-text mode) only plain fields are shown. Labels come
 * from Spark's `translations.json` (`referenceData.address.*`, shipped by the reference-data client layer). The value
 * is a flat {@link SparkAddress}; `Verified` says whether the ids came from reference data.
 *
 * Usable as a form control (`ControlValueAccessor`) or with `[(value)]`.
 */
@Component({
  selector: 'spark-address-input',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './spark-address-input.component.html',
  styleUrl: './spark-address-input.component.scss',
  imports: [BsTypeaheadComponent, TranslateKeyPipe],
  providers: [{ provide: NG_VALUE_ACCESSOR, useExisting: forwardRef(() => SparkAddressInputComponent), multi: true }],
})
export class SparkAddressInputComponent implements ControlValueAccessor {
  private readonly api = inject(ReferenceDataApi);
  private readonly language = inject(SparkLanguageService);
  private readonly destroyRef = inject(DestroyRef);

  /** The address. */
  readonly value = model<SparkAddress | null>(null);
  /** The language of every request and of the country names; unset = the Spark UI language. */
  readonly lang = input<string | null>(null);
  /** Countries listed first in the picker. */
  readonly preferredCountries = input<readonly string[]>(['BE', 'NL', 'FR', 'LU', 'DE']);
  /** The country a new address starts in. */
  readonly defaultCountry = input<string>('BE');
  /** Disabled by the form. */
  readonly disabled = signal(false);

  protected readonly id = `spark-address-${nextId++}`;
  protected readonly enabled = this.api.enabled;
  protected readonly uiLang = computed(() => this.lang() || this.language.language() || 'en');
  protected readonly countries = computed(() => countryChoices(this.uiLang(), this.preferredCountries()));
  protected readonly country = computed(() => (this.value()?.CountryCode ?? this.defaultCountry()).toUpperCase());
  /** Plain fields: free-text mode, or the viewer chose "not listed". */
  protected readonly freeText = signal(!this.api.enabled);
  /** The street box narrows within a picked locality, so it waits for one. */
  protected readonly streetBlocked = computed(() => this.disabled() || (!this.value()?.MunicipalityId && !this.value()?.PostalCode));

  private readonly profile = toSignal(
    toObservable(this.country).pipe(switchMap(cc => this.enabled ? this.api.profile(cc) : of(null))),
    { initialValue: null as CountryProfileResponse | null });

  protected readonly lines = computed(() => renderAddressLines(this.value(), this.profile()?.addressLines));
  protected readonly hasBox = computed(() => !!this.profile()?.boxPattern || !this.profile());

  // ---- the locality typeahead ----
  protected readonly localityText = signal('');
  private readonly localityQuery = new Subject<string>();
  protected readonly localities = signal<AddressSuggestion<SuggestItem>[]>([]);

  // ---- the street typeahead ----
  protected readonly streetText = signal('');
  private readonly streetQuery = new Subject<string>();
  protected readonly streets = signal<AddressSuggestion<StreetSuggestItem>[]>([]);

  private onChange: (value: SparkAddress | null) => void = () => {};
  private onTouched: () => void = () => {};

  constructor() {
    this.api.typeahead(this.localityQuery, q => this.api
      .suggest(q, { country: this.country().toLowerCase(), lang: this.uiLang(), asOf: localDate(), limit: 10 })
      .pipe(map(response => response?.items)))
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(items => this.localities.set(items.map(item => ({ text: this.suggestionText(item), item }))));

    this.api.typeahead(this.streetQuery, q => {
      const v = this.value();
      if (!v?.MunicipalityId && !v?.PostalCode) return of([]);
      return this.api
        .streets(q, { municipality: v?.MunicipalityId, postalCode: v?.PostalCode, lang: this.uiLang(), limit: 10 })
        .pipe(map(response => response?.items));
    })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(items => this.streets.set(items.map(item => ({ text: item.qualifier ? `${item.label} (${item.qualifier})` : item.label, item }))));
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

  /** The box's text changed (typing, or a pick writing the suggestion's text). */
  protected onLocalityText(text: string): void {
    this.localityText.set(text);
    this.onTouched();
  }

  protected searchLocality(text: string): void {
    this.localityQuery.next(text);
  }

  protected pickLocality(suggestion: AddressSuggestion<SuggestItem>): void {
    const item = suggestion.item;
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
    this.streetText.set('');
  }

  protected onStreetText(text: string): void {
    this.streetText.set(text);
    // Typing changes the street: it is free text until a suggestion is picked.
    this.patch({ StreetName: text, StreetId: null });
    this.onTouched();
  }

  protected searchStreet(text: string): void {
    this.streetQuery.next(text);
  }

  protected pickStreet(suggestion: AddressSuggestion<StreetSuggestItem>): void {
    this.patch({ StreetId: suggestion.item.id, StreetName: suggestion.item.label });
    this.streetText.set(suggestion.item.label);
  }

  protected setField(field: keyof SparkAddress, text: string): void {
    this.patch({ [field]: text } as Partial<SparkAddress>);
  }

  protected toggleFreeText(on: boolean): void {
    this.freeText.set(on || !this.enabled);
    if (on) this.patch({ Verified: false, PostalCodeId: null, StreetId: null, LocalityKey: null, LocalityPlaceId: null });
  }

  /** One line per suggestion: the label, the secondary line, and the "ended" hint (bs-typeahead renders text only). */
  private suggestionText(item: SuggestItem): string {
    let text = item.secondary ? `${item.label} · ${item.secondary}` : item.label;
    if (item.status === 'ended') text += ` (${this.language.t('referenceData.address.ended', 'ended')})`;
    return text;
  }

  private patch(change: Partial<SparkAddress>): void {
    const next: SparkAddress = { ...(this.value() ?? { CountryCode: this.country() }), ...change };
    next.Lines = renderAddressLines(next, this.profile()?.addressLines);
    const value = isEmptyAddress(next) ? null : next;
    this.value.set(value);
    this.onChange(value);
  }
}
