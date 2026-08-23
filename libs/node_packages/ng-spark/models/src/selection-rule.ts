/**
 * Parses a custom action's `selectionRule` — a cardinality expression over the number
 * of selected rows — into a predicate.
 *
 * A port of the server's `SelectionRuleParser`, and the two MUST agree: they are tested
 * against one shared fixture (`selection-rule.fixture.json`) for exactly this reason.
 * Vidyano, where this grammar comes from, has the same algorithm in C# and JavaScript and
 * the two have already drifted — one throws on a non-numeric operand where the other
 * silently permits everything.
 *
 * Grammar: `X` is the count placeholder, whitespace is insignificant, terms split on `X`
 * are AND-combined (`1<X<5` is a range), operators are `<= >= < > != =` matched in that
 * order so `>=` is never read as `>`, and a number-first term is mirrored (`0<X` is `>0`).
 *
 * Client-side this only drives whether a button is disabled. The server enforces the same
 * rule independently — and neither is an authorization boundary: the action's grant is.
 */
export function parseSelectionRule(rule?: string | null): (count: number) => boolean {
  if (!rule || !rule.trim()) return () => true;
  try {
    return compile(rule);
  } catch {
    // Unlike the server, which refuses to start on a malformed rule, the client cannot
    // usefully fail: the rule arrived over the wire from a server that already validated
    // it. Disabling the button is the safe direction — it never permits an action the
    // server would refuse.
    return () => false;
  }
}

const OPERATORS = ['<=', '>=', '<', '>', '!=', '='] as const;

function compile(rule: string): (count: number) => boolean {
  const normalized = rule.replace(/ /g, '').toUpperCase();
  const terms = normalized.split('X').filter(t => t.length > 0);
  if (terms.length === 0) throw new Error(`Selection rule '${rule}' has no condition.`);

  const numberFirst = !normalized.startsWith('X') && normalized.includes('X');
  const predicates = terms.map((term, i) => compileTerm(term, rule, numberFirst && i === 0));

  return count => predicates.every(p => p(count));
}

function compileTerm(term: string, rule: string, mirrored: boolean): (count: number) => boolean {
  const op = OPERATORS.find(o => (mirrored ? term.endsWith(o) : term.startsWith(o)));
  if (!op) throw new Error(`Selection rule '${rule}' has no recognised operator in '${term}'.`);

  const numberPart = mirrored ? term.slice(0, term.length - op.length) : term.slice(op.length);
  // Number(' ') is 0 and Number('1.5') is 1.5 — neither is a valid operand here.
  if (!/^-?\d+$/.test(numberPart)) {
    throw new Error(`Selection rule '${rule}' has a non-numeric operand in '${term}'.`);
  }
  const value = Number(numberPart);
  const effective = mirrored ? mirror(op) : op;

  switch (effective) {
    case '<=': return count => count <= value;
    case '>=': return count => count >= value;
    case '<': return count => count < value;
    case '>': return count => count > value;
    case '!=': return count => count !== value;
    case '=': return count => count === value;
    default: throw new Error(`Selection rule '${rule}' has an unsupported operator '${effective}'.`);
  }
}

function mirror(op: string): string {
  switch (op) {
    case '<': return '>';
    case '>': return '<';
    case '<=': return '>=';
    case '>=': return '<=';
    default: return op;
  }
}
