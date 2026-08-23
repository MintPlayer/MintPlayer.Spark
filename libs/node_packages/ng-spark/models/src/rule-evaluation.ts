import { ValidationRule } from './validation-rule';
import { TranslatedString } from './translated-string';

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
  if (Array.isArray(value)) return value.length === 0;
  return false;
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
  const label = attr.label?.['en'] ?? attr.name;

  if (attr.isRequired && isEmpty(value)) {
    return [{ attributeName: attr.name, ruleType: 'required', message: `${label} is required` }];
  }

  // A rule other than "required" says nothing about an absent value — that is what required is for.
  if (isEmpty(value)) return failures;

  const text = value?.toString() ?? '';

  for (const rule of attr.rules ?? []) {
    const failure = evaluateRule(attr.name, label, rule, value, text);
    if (failure) failures.push(failure);
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
  const message = rule.message?.['en'];

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

function toInt(value: any): number | null {
  const n = Number(value);
  return Number.isFinite(n) ? Math.trunc(n) : null;
}

function toNumber(value: any): number | null {
  const n = Number(value);
  return Number.isFinite(n) ? n : null;
}
