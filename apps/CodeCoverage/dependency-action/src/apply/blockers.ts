import type { Blocker } from '../contracts/types';

/**
 * Resolver-error parsers (R2), built on the REAL captures of S13/S14 (test/fixtures/resolver-errors).
 * Structured where the tool has it (NuGet project.assets.json logs[], pnpm ndjson, yarn --json),
 * regex over prose otherwise. Unrecognised output -> [] ("no blockers"), and the bot falls back to
 * "retry when any package changes".
 *
 * A blocker names the package whose version is in the way, so the bot can lift the hold when a newer
 * version of THAT package appears.
 */

const MAX_MESSAGE = 1500;

function clip(s: string): string {
  const t = s.trim();
  return t.length > MAX_MESSAGE ? `${t.slice(0, MAX_MESSAGE - 1)}…` : t;
}

function dedupe(blockers: Blocker[]): Blocker[] {
  const seen = new Set<string>();
  return blockers.filter((b) => {
    const k = `${b.depName}|${b.code}|${b.version ?? ''}`;
    if (seen.has(k)) return false;
    seen.add(k);
    return true;
  });
}

function stripNpmPrefix(text: string): string {
  return text
    .split(/\r?\n/)
    .map((l) => l.replace(/^npm (error|warn|ERR!|WARN) ?/, ''))
    .join('\n');
}

/**
 * npm ERESOLVE (error form, and the `--json` {error:{code,detail}} form) and the "overriding peer
 * dependency" WARNING form (S14), which D-C2 turns into a blocker: with --strict-peer-deps it is an
 * error, without it npm writes a lockfile that later fails.
 */
export function parseNpm(output: string, planned: readonly string[] = []): Blocker[] {
  const out: Blocker[] = [];
  let text = output;
  const json = output.match(/\{\s*"error"\s*:\s*\{[\s\S]*?\n\}/);
  if (json) {
    try {
      const parsed = JSON.parse(json[0]) as { error?: { code?: string; detail?: string } };
      if (parsed.error?.detail) text = `${parsed.error.code ?? ''}\n${parsed.error.detail}\n${output}`;
    } catch {
      /* fall through to prose */
    }
  }
  const plain = stripNpmPrefix(text);
  if (!/ERESOLVE/.test(plain)) {
    const notarget = plain.match(/(?:code )?ETARGET[\s\S]*?No matching version found for (\S+?)@(\S+?)\.?(\s|$)/);
    if (notarget) out.push({ depName: notarget[1], version: notarget[2], code: 'ETARGET', message: clip(notarget[0]) });
    return out;
  }
  // "Could not resolve dependency:\npeer react@"^18.2.0" from react-dom@18.2.0"
  const peerRe = /Could not resolve dependency:\s*\n\s*peer (\S+?)@"([^"]+)" from (\S+?)@(\S+)/g;
  let m: RegExpExecArray | null;
  const found = plain.match(/Found: (\S+?)@(\S+)/);
  while ((m = peerRe.exec(plain)) !== null) {
    const [, peer, range, from, fromVersion] = m;
    // The package that blocks is the one declaring the peer range (it needs a newer release: Angular N+1
    // before @nx/angular supports it). When that declarer is one of OUR planned packages, the blocker is
    // the peer it wants (react-dom 18 wants react 18; react 17 is what is in the way).
    const declarerPlanned = planned.includes(from);
    const foundPeer = found && found[1] === peer ? found : null;
    out.push({
      depName: declarerPlanned ? peer : from,
      version: declarerPlanned ? (foundPeer?.[2] ?? range) : fromVersion,
      code: 'ERESOLVE',
      message: clip(`peer ${peer}@"${range}" from ${from}@${fromVersion}${found ? `; found ${found[1]}@${found[2]}` : ''}`),
    });
  }
  if (out.length === 0) out.push({ depName: found?.[1] ?? null, version: found?.[2] ?? null, code: 'ERESOLVE', message: clip(plain.slice(0, 600)) });
  return dedupe(out);
}

/** pnpm `--reporter=ndjson`: the pnpm:peer-dependency-issues event (structured). */
export function parsePnpmNdjson(output: string, planned: readonly string[] = []): Blocker[] {
  const out: Blocker[] = [];
  /** Same rule as npm: a planned declarer means the peer it wants is what blocks. */
  const attribute = (peer: string, found: string | undefined, parent?: { name: string; version: string }) =>
    parent && planned.includes(parent.name) ? { depName: peer, version: found ?? null } : { depName: parent?.name ?? peer, version: parent?.version ?? null };
  for (const line of output.split(/\r?\n/)) {
    if (!line.startsWith('{')) continue;
    let ev: Record<string, unknown>;
    try {
      ev = JSON.parse(line) as Record<string, unknown>;
    } catch {
      continue;
    }
    if (ev.name === 'pnpm:peer-dependency-issues') {
      const projects = (ev.issuesByProjects ?? {}) as Record<string, { bad?: Record<string, Array<{ foundVersion?: string; wantedRange?: string; parents?: Array<{ name: string; version: string }> }>>; missing?: Record<string, Array<{ wantedRange?: string; parents?: Array<{ name: string; version: string }> }>> }>;
      for (const issues of Object.values(projects)) {
        for (const [peer, list] of Object.entries(issues.bad ?? {})) {
          for (const issue of list) {
            const parent = issue.parents?.[issue.parents.length - 1];
            out.push({
              ...attribute(peer, issue.foundVersion, parent),
              code: 'ERR_PNPM_PEER_DEP_ISSUES',
              message: clip(`unmet peer ${peer}@${issue.wantedRange ?? '?'}: found ${issue.foundVersion ?? '?'}${parent ? ` (required by ${parent.name}@${parent.version})` : ''}`),
            });
          }
        }
        for (const [peer, list] of Object.entries(issues.missing ?? {})) {
          for (const issue of list) {
            const parent = issue.parents?.[issue.parents.length - 1];
            out.push({ depName: parent?.name ?? peer, version: parent?.version ?? null, code: 'ERR_PNPM_PEER_DEP_ISSUES', message: clip(`missing peer ${peer}@${issue.wantedRange ?? '?'}`) });
          }
        }
      }
    } else if (ev.level === 'error' && typeof ev.err === 'object' && ev.err) {
      const err = ev.err as { code?: string; message?: string };
      const m = err.message?.match(/No matching version found for (\S+?)@(\S+)/);
      out.push({ depName: m?.[1] ?? null, version: m?.[2] ?? null, code: err.code ?? 'pnpm', message: clip(err.message ?? '') });
    }
  }
  // Text form ("✕ unmet peer evil-pkg@^1.0.0: found 2.0.0") when ndjson was not used.
  if (out.length === 0) {
    const re = /unmet peer (\S+?)@(\S+?): found (\S+)/g;
    let m: RegExpExecArray | null;
    const parent = output.match(/┬ (\S+) (\S+)\s*\n\s*└──? ✕ unmet peer/);
    while ((m = re.exec(output)) !== null) {
      out.push({ ...attribute(m[1], m[3], parent ? { name: parent[1], version: parent[2] } : undefined), code: 'ERR_PNPM_PEER_DEP_ISSUES', message: clip(m[0]) });
    }
  }
  return dedupe(out);
}

/** yarn berry `--json` NDJSON: YN0060 (incompatible peer) / YN0002 (missing peer) / YN0001 / YN0082. */
export function parseYarnJson(output: string, planned: readonly string[] = []): Blocker[] {
  const out: Blocker[] = [];
  for (const line of output.split(/\r?\n/)) {
    if (!line.startsWith('{')) continue;
    let ev: { type?: string; name?: number | null; displayName?: string; data?: string };
    try {
      ev = JSON.parse(line);
    } catch {
      continue;
    }
    const data = ev.data ?? '';
    if (ev.displayName === 'YN0060') {
      // "react is listed by your project with version 17.0.2 (pcb45e), which doesn't satisfy what react-dom requests (^18.2.0)."
      const m = data.match(/^(\S+) is listed by your project with version (\S+) .*? what (\S+) requests \(([^)]+)\)/);
      const declarerPlanned = !!m && planned.includes(m[3]);
      out.push({ depName: (declarerPlanned ? m?.[1] : m?.[3]) ?? null, version: declarerPlanned ? m![2] : null, code: 'YN0060', message: clip(data) });
    } else if (ev.displayName === 'YN0002') {
      const m = data.match(/^(\S+?)@\S+ doesn't provide (\S+)/);
      out.push({ depName: m?.[1] ?? null, version: null, code: 'YN0002', message: clip(data) });
    } else if (ev.displayName === 'YN0082' || (ev.type === 'error' && ev.displayName)) {
      const m = data.match(/No candidates found for (\S+?)@(?:npm:)?(\S+)/) ?? data.match(/(\S+?)@npm:(\S+?): No candidates found/);
      out.push({ depName: m?.[1] ?? null, version: m?.[2] ?? null, code: ev.displayName ?? 'yarn', message: clip(data) });
    }
  }
  return dedupe(out);
}

/** NuGet `obj/project.assets.json` logs[] (structured: code, level, libraryId). Errors only. */
export function parseNuGetAssets(assetsJson: string): Blocker[] {
  let doc: { logs?: Array<{ code?: string; level?: string; message?: string; libraryId?: string }> };
  try {
    doc = JSON.parse(assetsJson);
  } catch {
    return [];
  }
  return dedupe(
    (doc.logs ?? [])
      .filter((l) => (l.level ?? '').toLowerCase() === 'error')
      .map((l) => ({ depName: l.libraryId ?? nugetIdFromMessage(l.message ?? ''), version: nugetVersionFromMessage(l.message ?? ''), code: l.code ?? 'NU', message: clip(l.message ?? '') })),
  );
}

function nugetIdFromMessage(msg: string): string | null {
  return (
    msg.match(/Detected package downgrade: (\S+) from/)?.[1] ??
    msg.match(/Unable to find package (\S+)/)?.[1] ??
    msg.match(/Version conflict detected for (\S+)\./)?.[1] ??
    msg.match(/Package (\S+) \S+ is not compatible/)?.[1] ??
    null
  );
}

function nugetVersionFromMessage(msg: string): string | null {
  return msg.match(/Detected package downgrade: \S+ from (\S+) to/)?.[1] ?? msg.match(/with version \(([^)]+)\)/)?.[1] ?? null;
}

/** NuGet restore console output, when no project.assets.json is available. */
export function parseNuGetText(output: string): Blocker[] {
  const out: Blocker[] = [];
  const re = /error (NU\d{4}): ([^\r\n]*)/g;
  let m: RegExpExecArray | null;
  while ((m = re.exec(output)) !== null) {
    const id = nugetIdFromMessage(m[2]);
    // Continuation lines ("App -> X 6.0.0 -> Serilog (>= 4.0.0)") repeat the code without a package.
    if (!id) continue;
    out.push({ depName: id, version: nugetVersionFromMessage(m[2]), code: m[1], message: clip(m[2]) });
  }
  return dedupe(out);
}

export function parseComposer(output: string, planned: readonly string[] = []): Blocker[] {
  const out: Blocker[] = [];
  const blocks = output.split(/(?:^|\n)[ \t]*Problem \d+[ \t]*\r?\n/).slice(1);
  for (const block of blocks) {
    const lines = block.split('\n').filter((l) => /^\s*- /.test(l));
    const text = lines.join('\n');
    if (!text) continue;
    // "- monolog/monolog 3.5.0 requires psr/log ^2.0 || ^3.0 -> found psr/log[...] but it conflicts with your root composer.json require (^1.1)."
    const req = text.match(/- (\S+\/\S+) (\S+) requires (\S+\/\S+) ([^-\n]+?) -> /);
    const root = text.match(/Root composer\.json requires (\S+\/\S+) (\S+)/);
    // A planned package that requires something the root pins: the required package blocks.
    const requirerPlanned = !!req && planned.includes(req[1]);
    out.push({
      depName: (requirerPlanned ? req![3] : req?.[1] ?? root?.[1]) ?? null,
      version: requirerPlanned ? req![4].trim() : (req?.[2] ?? root?.[2] ?? null),
      code: 'composer',
      message: clip(text),
    });
  }
  return dedupe(out);
}

export function parseCargo(output: string, planned: readonly string[] = []): Blocker[] {
  const m = output.match(/failed to select a version for `([^`]+)`[\s\S]*?required by package `(\S+) v(\S+)`/);
  if (m) {
    // serde 1.0.200 (planned) needs a serde_derive the root pins: serde_derive blocks.
    const previous = output.match(/previously selected package `(\S+) v(\S+)`/);
    const planned2 = planned.includes(m[2]);
    return [{
      depName: planned2 ? m[1] : m[2],
      version: planned2 ? (previous && previous[1] === m[1] ? previous[2] : null) : m[3],
      code: 'cargo',
      message: clip(`failed to select a version for ${m[1]} (required by ${m[2]} v${m[3]})${previous ? `; previously selected ${previous[1]} v${previous[2]}` : ''}`),
    }];
  }
  const nf = output.match(/no matching package named `([^`]+)` found|failed to select a version for the requirement `(\S+) = "([^"]+)"`/);
  if (nf) return [{ depName: nf[1] ?? nf[2], version: nf[3] ?? null, code: 'cargo', message: clip(nf[0]) }];
  return [];
}

export function parseGo(output: string): Blocker[] {
  const tc = output.match(/go: (\S+)@(\S+) requires go >= (\S+) \(running go (\S+)/);
  if (tc) return [{ depName: 'go', version: tc[3], code: 'go-toolchain', message: clip(tc[0]) }];
  const nf = output.match(/go: (\S+?)@(\S+?): [^\n]*?(?:no such file or directory|unknown revision|invalid version|not found)[^\n]*/);
  if (nf) return [{ depName: nf[1], version: nf[2], code: 'go-missing-version', message: clip(nf[0]) }];
  return [];
}

export function parseMaven(output: string): Blocker[] {
  const out: Blocker[] = [];
  const re = /Could not find artifact (\S+?):(\S+?):(?:\S+?:)?(\S+) in (\S+)/g;
  let m: RegExpExecArray | null;
  while ((m = re.exec(output)) !== null) {
    out.push({ depName: `${m[1]}:${m[2]}`, version: m[3], code: 'maven-missing-artifact', message: clip(m[0]) });
  }
  if (out.length === 0) {
    const r = output.match(/The following artifacts could not be resolved: (\S+?):(\S+?):\S+?:(\S+?) /);
    if (r) out.push({ depName: `${r[1]}:${r[2]}`, version: r[3], code: 'maven-unresolved', message: clip(r[0]) });
  }
  return dedupe(out);
}

/** Gradle exits 0 while printing FAILED (S13), so this doubles as the failure detector. */
export function parseGradle(output: string): Blocker[] {
  const out: Blocker[] = [];
  const re = /^[\s|\\+-]*([\w.-]+):([\w.-]+):(\S+?) FAILED\s*$/gm;
  let m: RegExpExecArray | null;
  while ((m = re.exec(output)) !== null) out.push({ depName: `${m[1]}:${m[2]}`, version: m[3], code: 'gradle-FAILED', message: clip(m[0].trim()) });
  return dedupe(out);
}

export function gradleFailed(output: string): boolean {
  return / FAILED\s*$/m.test(output) || /BUILD FAILED/.test(output);
}

export function parsePip(output: string): Blocker[] {
  if (!/ResolutionImpossible|conflicting dependencies/.test(output)) return [];
  const out: Blocker[] = [];
  const cause = output.split(/The conflict is caused by:/)[1] ?? '';
  const re = /^\s+(\S+) (\S+) depends on ([^\n]+)$/gm;
  let m: RegExpExecArray | null;
  while ((m = re.exec(cause)) !== null) out.push({ depName: m[1], version: m[2], code: 'ResolutionImpossible', message: clip(m[0]) });
  if (out.length === 0) out.push({ depName: null, version: null, code: 'ResolutionImpossible', message: clip(cause || output.slice(-800)) });
  return dedupe(out);
}

export function parsePoetry(output: string): Blocker[] {
  if (!/version solving failed|SolverProblemError/.test(output)) return [];
  const m = output.match(/depends on (\S+) \(([^)]+)\) which depends on (\S+) \(([^)]+)\)/);
  if (m) return [{ depName: m[1], version: m[2], code: 'SolverProblemError', message: clip(output.match(/Because[\s\S]*?failed\./)?.[0] ?? m[0]) }];
  return [{ depName: null, version: null, code: 'SolverProblemError', message: clip(output.match(/Because[\s\S]*?failed\./)?.[0] ?? output.slice(-800)) }];
}

export function parseUv(output: string): Blocker[] {
  if (!/No solution found when resolving dependencies/.test(output)) return [];
  const cause = output.match(/cause: ([\s\S]*?)(\n\s*\n|$)/)?.[1] ?? output;
  const noVersion = cause.match(/there is no version of (\S+?)==(\S+?) /);
  if (noVersion) return [{ depName: noVersion[1], version: noVersion[2], code: 'uv-no-version', message: clip(cause) }];
  const noWheel = cause.match(/Because (\S+?)==(\S+?) has no usable wheels/);
  if (noWheel) return [{ depName: noWheel[1], version: noWheel[2], code: 'uv-no-build', message: clip(cause + (output.match(/hint: [^\n]+/)?.[0] ? `\n${output.match(/hint: [^\n]+/)![0]}` : '')) }];
  const dep = cause.match(/Because (\S+?)==(\S+?) depends on/);
  return [{ depName: dep?.[1] ?? null, version: dep?.[2] ?? null, code: 'uv-no-solution', message: clip(cause) }];
}
