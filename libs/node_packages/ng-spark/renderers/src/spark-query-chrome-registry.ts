import { InjectionToken, InputSignal, Provider, Type } from '@angular/core';
import { SparkQuery } from '@mintplayer/ng-spark/models';

/**
 * The contract a query header component may implement.
 *
 * Every member is optional and is filtered through `withDeclaredInputs` before being
 * handed to `NgComponentOutlet` — which throws on an input the component did not
 * declare — so a header that only wants `query` declares only `query`.
 *
 * `reload` arrives as an input callback rather than an output, because
 * `NgComponentOutlet` cannot bind outputs. `SparkAttributeEditRenderer.valueChange`
 * does the same thing for the same reason.
 */
export interface SparkQueryHeaderRenderer {
  /** The query this header belongs to. */
  query?: InputSignal<SparkQuery | undefined>;
  /** Whatever `headerRendererOptions` the model file declared. */
  options?: InputSignal<Record<string, unknown> | undefined>;
  /** Re-runs the query, keeping page and sort. For a header that mutates server state. */
  reload?: InputSignal<() => void>;
}

export interface SparkQueryChromeRegistration {
  /** Must match `headerRenderer` on the query in the model JSON. */
  name: string;
  /** Rendered in place of the caption AND the action bar. Should implement {@link SparkQueryHeaderRenderer}. */
  headerComponent: Type<any>;
}

export const SPARK_QUERY_CHROME = new InjectionToken<SparkQueryChromeRegistration[]>(
  'SparkQueryChrome',
  // Always resolves, so a host that registers nothing is not a special case —
  // matching SPARK_ATTRIBUTE_RENDERERS.
  { factory: () => [] }
);

/**
 * Register query header components globally.
 *
 * The header is declared by the QUERY, not passed by the host, because a sub-query is
 * rendered automatically from `EntityTypeDefinition.Queries` — in that call site there
 * is no host to hand it anything, and it is the common one.
 *
 * @example
 * provideSparkQueryChrome([
 *   { name: 'accounts-header', headerComponent: AccountsHeaderComponent },
 * ])
 */
export function provideSparkQueryChrome(
  chrome: SparkQueryChromeRegistration[]
): Provider {
  return {
    provide: SPARK_QUERY_CHROME,
    useValue: chrome,
  };
}
