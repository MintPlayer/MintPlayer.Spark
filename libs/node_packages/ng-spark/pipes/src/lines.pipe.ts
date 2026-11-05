import { Pipe, PipeTransform } from '@angular/core';

/**
 * A multiline value's lines, for a template that writes a `<br>` between them. `{{ }}` writes `\n` as a plain line break
 * in one text node, which HTML collapses into a space; `white-space: pre-wrap` kept it on screen, but an HTML minifier
 * (`MintPlayer.Spark.Minification`, #563) cannot see CSS and collapsed it in the server-rendered page. A `<br>` is markup,
 * so every minifier and crawler keeps it. `\r\n`, `\r` and `\n` all break a line; anything that is not a string is one line.
 */
export function sparkLines(value: unknown): string[] {
  if (value === null || value === undefined) return [];
  return String(value).split(/\r\n|\r|\n/);
}

@Pipe({ name: 'lines', standalone: true, pure: true })
export class LinesPipe implements PipeTransform {
  transform(value: unknown): string[] {
    return sparkLines(value);
  }
}
