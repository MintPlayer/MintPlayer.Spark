import { ValidationRule } from './generated/contracts';
import { DEFAULT_LANGUAGE, TranslatedString, isCurrentEntry } from './translated-string';

/** One rule failure, in the shape the form already renders per field. */
export interface RuleFailure {
  attributeName: string;
  ruleType: string;
  message: string;
}

export interface EvaluableAttribute {
  name: string;
  label?: TranslatedString;
  isRequired?: boolean;
  rules?: ValidationRule[];
}

const EMAIL = /^[^@\s]+@[^@\s]+\.[^@\s]+$/i;
const URL = /^https?:\/\/[^\s]+$/i;

function isEmpty(value: any): boolean {
  if (value === null || value === undefined) return true;
  if (typeof value === 'string') return value.trim() === '';
  // A list of blank inputs is as empty as no list: the user added items and typed nothing.
  if (Array.isArray(value)) return value.every(isEmpty);
  return false;
}

/** A list item a value rule can judge: a plain value, not an embedded row. */
function isRuleItem(value: any): boolean {
  return !isEmpty(value) && (typeof value !== 'object' || value instanceof Date);
}

/**
 * Evaluates an attribute's rules against a value, mirroring the server's `ValidationService`.
 *
 * ⚠️ **Parity with the server is the point, and disagreement is worse than silence.** A client that
 * rejects something the server would accept blocks legitimate work with no recourse; one that
 * accepts something the server rejects merely defers the error to the round-trip, which is where it
 * used to live anyway. So the rule set here is deliberately limited to the types the server
 * implements, and an unrecognised rule type is ignored rather than guessed at.
 *
 * This exists because a refresh hook that imposes a rule needs it to bite before Save — previously
 * `rules` was carried on the wire and never evaluated in the browser at all.
 */
export function evaluateRules(attr: EvaluableAttribute, value: any): RuleFailure[] {
  const failures: RuleFailure[] = [];
  const label = defaultLanguageText(attr.label) ?? attr.name;

  if (attr.isRequired && isEmpty(value)) {
    return [{ attributeName: attr.name, ruleType: 'required', message: `${label} is required` }];
  }

  // A rule other than "required" says nothing about an absent value — that is what required is for.
  if (isEmpty(value)) return failures;

  // A list of plain values (`List<string>`, `int[]`) is judged per item: a `maxLength` limits each
  // entry, not the comma-joined text `toString()` would give, and `range` judges each number. Blank
  // items are skipped as a blank scalar is. One failure per rule — the first item that breaks it —
  // since the form shows errors per attribute. Mirrors the server's `ValidationService`.
  const items = Array.isArray(value) ? value.filter(isRuleItem) : [value];

  for (const rule of attr.rules ?? []) {
    for (const item of items) {
      const failure = evaluateRule(attr.name, label, rule, item, item?.toString() ?? '');
      if (failure) {
        failures.push(failure);
        break;
      }
    }
  }

  return failures;
}

function evaluateRule(
  name: string,
  label: string,
  rule: ValidationRule,
  value: any,
  text: string,
): RuleFailure | null {
  const message = defaultLanguageText(rule.message);

  switch (rule.type?.toLowerCase()) {
    case 'maxlength': {
      const max = toInt(rule.value);
      if (max === null || text.length <= max) return null;
      return { attributeName: name, ruleType: 'maxLength', message: message ?? `${label} must be at most ${max} characters` };
    }
    case 'minlength': {
      const min = toInt(rule.value);
      if (min === null || text.length >= min) return null;
      return { attributeName: name, ruleType: 'minLength', message: message ?? `${label} must be at least ${min} characters` };
    }
    case 'range': {
      const numeric = toNumber(value);
      if (numeric === null) return null;
      if (rule.min !== undefined && rule.min !== null && numeric < rule.min) {
        return { attributeName: name, ruleType: 'range', message: message ?? `${label} must be at least ${rule.min}` };
      }
      if (rule.max !== undefined && rule.max !== null && numeric > rule.max) {
        return { attributeName: name, ruleType: 'range', message: message ?? `${label} must be at most ${rule.max}` };
      }
      return null;
    }
    case 'regex': {
      const pattern = rule.value?.toString();
      if (!pattern) return null;
      let re: RegExp;
      try {
        re = new RegExp(pattern);
      } catch {
        // An unparseable pattern is a server-side authoring bug. Staying silent leaves the server to
        // report it, which is strictly better than blocking the user over a rule nobody can evaluate.
        return null;
      }
      return re.test(text) ? null : { attributeName: name, ruleType: 'regex', message: message ?? `${label} is not in the expected format` };
    }
    case 'email':
      return EMAIL.test(text) ? null : { attributeName: name, ruleType: 'email', message: message ?? `${label} must be a valid email address` };
    case 'url':
      return URL.test(text) ? null : { attributeName: name, ruleType: 'url', message: message ?? `${label} must be a valid URL` };
    default:
      return null;
  }
}

/**
 * Mirrors the server's `TryGetIntValue`: an integer number, or a string `int.TryParse` accepts.
 * Anything else means "no rule". `Number(value)` is NOT that — it turns null and '' into 0 (so every
 * non-empty value failed "must be at most 0 characters") and truncated 2.5, which the server ignores.
 */
function toInt(value: any): number | null {
  if (typeof value === 'number') return Number.isInteger(value) ? value : null;
  if (typeof value === 'string' && /^\s*[+-]?\d+\s*$/.test(value)) return parseInt(value, 10);
  return null;
}

function toNumber(value: any): number | null {
  const n = Number(value);
  return Number.isFinite(n) ? n : null;
}

/**
 * The English (default-language) text only, never another language: these messages are composed in
 * English, so a label or message without an English entry falls back to the attribute name or the
 * default text rather than mixing languages into one sentence.
 */
function defaultLanguageText(ts: TranslatedString | null | undefined): string | undefined {
  const day = new Date().toISOString().substring(0, 10);
  return ts?.Entries?.find(e => e.Tag.toLowerCase() === DEFAULT_LANGUAGE && isCurrentEntry(e, day))?.Value;
}
