import type { PlatformLocation, PlatformLocationKind, Sdk } from '../contracts/types';
import { SDKS } from '../contracts/types';
import { parseText, type TextDoc } from '../core/lineEdit';
import { basename, isDockerfile, isWorkflowFile, type RepoFiles } from '../util/repoFiles';
import { channelOf, sortChannelsDesc, tfmChannel } from './channels';

/**
 * Platform location scanning (R7/R8, S19), shared by the lookup (platform records) and the apply
 * (platform groups). A location carries the exact token span, so the apply edits what the lookup
 * reported, and nothing else.
 */
export interface Found extends PlatformLocation {
  sdk: Sdk;
  line: number;
  /** Token span inside the line. */
  start: number;
  end: number;
  /** For tfm: the element name, so `strategy: add` can turn TargetFramework into TargetFrameworks. */
  element?: string;
  elementStart?: number;
  elementEnd?: number;
  closeStart?: number;
  closeEnd?: number;
  /** For docker: the image the tag belongs to (`mcr.microsoft.com/dotnet/sdk`), for the registry check (P7). */
  image?: string;
  /** For a docker ARG default: what the FROM appends to it (`-alpine` in `node:${NODE_VERSION}-alpine`), for the P7 check. */
  tagSuffix?: string;
  /** A line of a multi-line setup input (`dotnet-version: |`): one of several versions installed side by side (R7). */
  listed?: boolean;
  /** For tfm-property: the project files whose TFM resolves to this token (R4), so a template knows its projects moved (P8). */
  readers?: string[];
  /** For template-framework: the template.json key the token is under (`choice`, `description`, `replaces`, `defaultValue`). */
  templateKey?: string;
}

const DOCKER_IMAGES: Record<Sdk, RegExp> = {
  dotnet: /^(?:mcr\.microsoft\.com\/dotnet\/(?:nightly\/)?[\w.-]+)$/,
  node: /^(?:docker\.io\/)?(?:library\/)?node$/,
  python: /^(?:docker\.io\/)?(?:library\/)?python$/,
  java: /^(?:docker\.io\/)?(?:library\/)?(?:eclipse-temurin|openjdk|amazoncorretto|ibm-semeru-runtimes)$/,
  go: /^(?:docker\.io\/)?(?:library\/)?golang$/,
  php: /^(?:docker\.io\/)?(?:library\/)?php$/,
  ruby: /^(?:docker\.io\/)?(?:library\/)?ruby$/,
  rust: /^(?:docker\.io\/)?(?:library\/)?rust$/,
};

/** The sdk whose platform record owns this Docker image's tags (D-S12c), or null. */
export function platformOfImage(image: string): Sdk | null {
  for (const sdk of Object.keys(DOCKER_IMAGES) as Sdk[]) if (DOCKER_IMAGES[sdk].test(image)) return sdk;
  return null;
}

const SETUP_ACTIONS: Record<Sdk, { action: string; keys: string[] }> = {
  dotnet: { action: 'actions/setup-dotnet', keys: ['dotnet-version'] },
  node: { action: 'actions/setup-node', keys: ['node-version'] },
  python: { action: 'actions/setup-python', keys: ['python-version'] },
  java: { action: 'actions/setup-java', keys: ['java-version'] },
  go: { action: 'actions/setup-go', keys: ['go-version'] },
  php: { action: 'shivammathur/setup-php', keys: ['php-version'] },
  ruby: { action: 'ruby/setup-ruby', keys: ['ruby-version'] },
  rust: { action: 'actions-rust-lang/setup-rust-toolchain', keys: ['toolchain'] },
};

const VERSION_FILES: Record<Sdk, string[]> = {
  dotnet: [],
  node: ['.nvmrc', '.node-version'],
  python: ['.python-version'],
  java: ['.java-version'],
  go: ['.go-version'],
  php: ['.php-version'],
  ruby: ['.ruby-version'],
  // The legacy toolchain file holds just the channel; rust-toolchain.toml is scanned on its own.
  rust: ['rust-toolchain'],
};

const TOOL_VERSIONS_NAMES: Record<Sdk, string[]> = {
  dotnet: ['dotnet', 'dotnet-core'],
  node: ['nodejs', 'node'],
  python: ['python'],
  java: ['java'],
  go: ['golang', 'go'],
  php: ['php'],
  ruby: ['ruby'],
  rust: ['rust'],
};

function loc(sdk: Sdk, file: string, kind: PlatformLocationKind, line: number, start: number, value: string, opts: Partial<Found> = {}): Found {
  const computed = opts.computed ?? /\$\(|\$\{/.test(value);
  return {
    sdk,
    file,
    kind,
    value,
    line,
    start,
    end: start + value.length,
    channel: opts.channel !== undefined ? opts.channel : channelOf(sdk, value),
    computed,
    reason: opts.reason ?? (computed ? 'computed value' : null),
    ...opts,
  } as Found;
}

function scanTfm(file: string, doc: TextDoc): Found[] {
  const out: Found[] = [];
  doc.lines.forEach((l, i) => {
    const re = /<(TargetFrameworks?)(\s[^>]*)?>([^<]*)<\/(TargetFrameworks?)>/g;
    let m: RegExpExecArray | null;
    while ((m = re.exec(l.text)) !== null) {
      const [, element, attrs = '', value] = m;
      const valueStart = m.index + 1 + element.length + attrs.length + 1;
      const conditioned = /\bCondition\s*=/.test(attrs);
      // `${X}`: a `*.csproj.in` placeholder that Arcade's GenerateFileFromTemplate fills from an MSBuild property.
      const computed = conditioned || /\$[({]/.test(value);
      const channels = value
        .split(';')
        .map((t) => tfmChannel(t))
        .filter((c): c is string => c !== null)
        .sort((a, b) => Number(b.split('.')[0]) - Number(a.split('.')[0]));
      out.push(
        loc('dotnet', file, 'tfm', i, valueStart, value, {
          channel: channels[0] ?? null,
          computed,
          reason: conditioned ? 'conditioned TFM (Condition attribute)' : computed ? 'computed TFM ($(Property))' : null,
          element,
          elementStart: m.index + 1,
          elementEnd: m.index + 1 + element.length,
          closeStart: m.index + m[0].length - 1 - element.length,
          closeEnd: m.index + m[0].length - 1,
        }),
      );
    }
    out.push(...scanTfmConditions(file, l.text, i));
  });
  return out;
}

/** `'$(TargetFramework)' == 'net10.0'` (or `!=`, either side): an equality test on the TFM, not a compatibility test. */
const TFM_EQUALITY = /\$\(TargetFramework\)['"]?\s*[!=]=|[!=]=\s*['"]?\$\(TargetFramework\)/;

/**
 * #606 S5: a `Condition` that compares the TFM with a literal (`<PropertyGroup Condition="'$(TargetFramework)' ==
 * 'net10.0'">`) goes dead when `strategy: replace` moves the TFM, and nothing fails until what it guarded is missed. It is
 * reported for review, never edited. `IsTargetFrameworkCompatible(...)` conditions survive a move and are left alone, as
 * are the conditions on a TargetFramework element itself, which {@link scanTfm} already reports.
 */
function scanTfmConditions(file: string, text: string, line: number): Found[] {
  const out: Found[] = [];
  const re = /<(\w+)\b[^>]*?\bCondition\s*=\s*(["'])(.*?)\2/g;
  let m: RegExpExecArray | null;
  while ((m = re.exec(text)) !== null) {
    const [whole, element, , condition] = m;
    if (/^TargetFrameworks?$/.test(element) || !TFM_EQUALITY.test(condition)) continue;
    const [channel] = sortChannelsDesc([...condition.matchAll(/'(net\d+\.\d+)'/g)].map((t) => tfmChannel(t[1])).filter((c): c is string => c !== null));
    if (channel === undefined) continue;
    const start = m.index + whole.length - 1 - condition.length;
    out.push(
      loc('dotnet', file, 'tfm', line, start, condition, {
        channel,
        computed: true,
        reason: `a ${element} condition tests the TFM for net${channel}: review it when the TFM moves`,
      }),
    );
  }
  return out;
}

/**
 * The project's OWN package version (`<Version>` / `<VersionPrefix>` in a PropertyGroup), for
 * `lockPackageMajor` (R8). A `<Version>` child of a PackageReference is a dependency, not this.
 */
function scanPackageVersion(file: string, doc: TextDoc): Found[] {
  const out: Found[] = [];
  let inReference = false;
  doc.lines.forEach((l, i) => {
    if (/<(PackageReference|PackageVersion|GlobalPackageReference)\b/.test(l.text) && !/\/>/.test(l.text) && !/<\/(PackageReference|PackageVersion|GlobalPackageReference)>/.test(l.text)) {
      inReference = true;
      return;
    }
    if (inReference) {
      if (/<\/(PackageReference|PackageVersion|GlobalPackageReference)>/.test(l.text)) inReference = false;
      return;
    }
    // platform-locations-PRD R5: `<PackageVersion>` with no attributes is the package's own version too; the CPM
    // `<PackageVersion Include=… Version=…/>` is a dependency (skipped above).
    const m = l.text.match(/<(Version|VersionPrefix|PackageVersion)>([^<]+)<\/(Version|VersionPrefix|PackageVersion)>/);
    if (!m || m.index === undefined) return;
    const value = m[2];
    const start = m.index + m[1].length + 2;
    const major = value.match(/^(\d+)\./);
    out.push(loc('dotnet', file, 'package-version', i, start, value, { channel: major ? `${major[1]}.0` : null }));
  });
  return out;
}

/** The brace depth change of one JSON line, ignoring braces inside strings. */
function braceDelta(text: string): number {
  let delta = 0;
  let inString = false;
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (inString) {
      if (c === '\\') i++;
      else if (c === '"') inString = false;
    } else if (c === '"') inString = true;
    else if (c === '{') delta++;
    else if (c === '}') delta--;
  }
  return delta;
}

/**
 * platform-locations-PRD R2/P8 (§5.3): in a `.template.config/template.json`, a `symbols` entry whose `datatype` is `choice`
 * and which offers a `netN.N` choice is the template's framework parameter. Every `netN.N` token under its `choice`,
 * `description`, `replaces` and `defaultValue` keys is one `template-framework` location. Other symbols, and other text,
 * are left alone.
 */
function scanTemplateJson(file: string, doc: TextDoc): Found[] {
  const out: Found[] = [];
  const blocks: Array<[number, number]> = [];
  let depth = 0;
  let symbolsDepth: number | null = null;
  let open: number | null = null;
  doc.lines.forEach((l, i) => {
    const before = depth;
    depth += braceDelta(l.text);
    if (symbolsDepth === null) {
      if (/"symbols"\s*:\s*\{/.test(l.text)) symbolsDepth = before + 1;
      return;
    }
    if (open === null && before === symbolsDepth && /^\s*"[^"]*"\s*:\s*\{/.test(l.text)) {
      if (depth === symbolsDepth) blocks.push([i, i]);
      else open = i;
    } else if (open !== null && depth === symbolsDepth) {
      blocks.push([open, i]);
      open = null;
    }
    if (depth < symbolsDepth) symbolsDepth = Number.NaN;
  });
  for (const [from, to] of blocks) {
    const lines = doc.lines.slice(from, to + 1);
    const text = lines.map((l) => l.text).join('\n');
    if (!/"datatype"\s*:\s*"choice"/.test(text) || !/"choice"\s*:\s*"net\d+\.\d+"/.test(text)) continue;
    lines.forEach((l, k) => {
      for (const m of l.text.matchAll(/"(choice|description|replaces|defaultValue)"\s*:\s*"((?:[^"\\]|\\.)*)"/g)) {
        const valueStart = m.index! + m[0].length - 1 - m[2].length;
        for (const t of m[2].matchAll(/(?<![\w.])net\d+\.\d+(?![\w.])/g)) {
          out.push(loc('dotnet', file, 'template-framework', from + k, valueStart + t.index!, t[0], { channel: tfmChannel(t[0]), templateKey: m[1] }));
        }
      }
    });
  }
  return out;
}

function scanJsonKey(sdk: Sdk, file: string, doc: TextDoc, key: string, kind: PlatformLocationKind, scope?: RegExp): Found[] {
  const out: Found[] = [];
  let inScope = !scope;
  let depth = 0;
  doc.lines.forEach((l, i) => {
    if (scope && !inScope && scope.test(l.text)) {
      inScope = true;
      depth = 0;
    }
    if (inScope) {
      const re = new RegExp(`"${key.replace(/[/.*+?^${}()|[\]\\]/g, '\\$&')}"\\s*:\\s*"([^"]*)"`);
      const m = l.text.match(re);
      if (m && m.index !== undefined) {
        const start = m.index + m[0].length - 1 - m[1].length;
        out.push(loc(sdk, file, kind, i, start, m[1]));
      }
      if (scope) {
        depth += (l.text.match(/{/g) ?? []).length - (l.text.match(/}/g) ?? []).length;
        if (depth <= 0 && /}/.test(l.text)) inScope = false;
      }
    }
  });
  return out;
}

function scanDocker(sdk: Sdk, file: string, doc: TextDoc): Found[] {
  const out: Found[] = [];
  // platform-locations-PRD R8 (§5.7): `ARG NODE_VERSION=22` read by `FROM node:${NODE_VERSION}`: the default is the location.
  const args = new Map<string, Array<{ line: number; start: number; value: string | null }>>();
  const resolved = new Map<string, Found>();
  doc.lines.forEach((l, i) => {
    const arg = l.text.match(/^(\s*ARG\s+)([A-Za-z_]\w*)(?:(=)([^\s"'$]+))?\s*$/i);
    if (arg) {
      const at = arg[1].length + arg[2].length + 1;
      args.set(arg[2], [...(args.get(arg[2]) ?? []), { line: i, start: at, value: arg[3] ? arg[4] : null }]);
      return;
    }
    // R8: NodeSource's `setup_22.x` installs Node 22 into another image (a RUN line or its continuation).
    if (sdk === 'node') {
      for (const s of l.text.matchAll(/deb\.nodesource\.com\/setup_(\d+)\.x\b/g)) {
        const start = s.index! + s[0].length - s[1].length - 2;
        out.push(loc('node', file, 'docker', i, start, s[1], { channel: s[1] }));
      }
    }
    const m = l.text.match(/^(\s*FROM\s+(?:--platform=\S+\s+)?)([^\s:@]+):([^\s@]+)(@sha256:[0-9a-f]{64})?/i);
    if (!m) return;
    if (!DOCKER_IMAGES[sdk].test(m[2])) return;
    const start = m[1].length + m[2].length + 1;
    const digest = !!m[4];
    const computed = digest || /\$/.test(m[3]);
    const ref = !digest ? m[3].match(/^\$\{?([A-Za-z_]\w*)\}?(.*)$/) : null;
    const defs = ref && !/\$/.test(ref[2]) ? args.get(ref[1]) ?? [] : [];
    const def = defs.length === 1 ? defs[0] : null;
    if (ref && def?.value && channelOf(sdk, def.value) !== null) {
      // One location per ARG, however many stages read it; the registry check looks at the first stage's tag.
      const key = `${def.line}`;
      if (!resolved.has(key)) {
        const found = loc(sdk, file, 'docker', def.line, def.start, def.value, { image: m[2], tagSuffix: ref[2] });
        resolved.set(key, found);
        out.push(found);
      }
      return;
    }
    out.push(
      loc(sdk, file, 'docker', i, start, m[3], {
        computed,
        image: m[2],
        reason: digest ? 'digest-pinned image (the bot plans image digests as docker records)' : computed ? 'computed tag (ARG)' : null,
      }),
    );
  });
  return out.sort((a, b) => a.line - b.line || a.start - b.start);
}

/**
 * platform-locations-PRD R8/P10: `runs.using: node20` in an action's metadata. One under `.github/` is found but never
 * written (the bot's patches change no file there but workflows), so {@link notEditedReason} reports it.
 */
function scanActionRuntime(file: string, doc: TextDoc): Found[] {
  const out: Found[] = [];
  let inRuns = false;
  doc.lines.forEach((l, i) => {
    if (/^runs\s*:/.test(l.text)) {
      inRuns = true;
      return;
    }
    if (/^\S/.test(l.text)) inRuns = false;
    const m = inRuns ? l.text.match(/^(\s+using\s*:\s*(['"]?))(node(\d+))\2\s*(#.*)?$/) : null;
    if (m) out.push(loc('node', file, 'action-runtime', i, m[1].length, m[3], { channel: m[4] }));
  });
  return out;
}

/**
 * #606 M6 (G6): a workflow's own images: `container: node:22`, `image: node:22-alpine` (under `container:` or
 * `services:`) and `uses: docker://node:22`. Kind `docker`, as a Dockerfile's FROM, so Renovate's docker record of the
 * same tag folds into the platform record (R31).
 */
function scanWorkflowImage(sdk: Sdk, file: string, doc: TextDoc): Found[] {
  const out: Found[] = [];
  doc.lines.forEach((l, i) => {
    const m = l.text.match(/^(\s*(?:-\s+)?(?:container|image|uses):\s*(['"]?)(docker:\/\/)?)([^\s:@'"]+):([^\s@'"#]+)(@sha256:[0-9a-f]{64})?\2\s*(#.*)?$/);
    if (!m || (/uses:/.test(m[1]) && !m[3]) || !DOCKER_IMAGES[sdk].test(m[4])) return;
    const digest = !!m[6];
    const computed = digest || /\$\{\{/.test(m[5]);
    out.push(
      loc(sdk, file, 'docker', i, m[1].length + m[4].length + 1, m[5], {
        computed,
        image: m[4],
        reason: digest ? 'digest-pinned image (the bot plans image digests as docker records)' : computed ? 'expression' : null,
      }),
    );
  });
  return out;
}

/**
 * #606 M6 (G6): the entries of a matrix list a setup-* step reads (`node-version: ${{ matrix.node }}` with
 * `node: [20, 22]` or a block list). Each entry is a location of kind `matrix`; the step's own value stays computed.
 */
function scanMatrix(sdk: Sdk, file: string, doc: TextDoc): Found[] {
  const keys = new Set(
    scanSetupAction(sdk, file, doc)
      .map((f) => f.value.match(/^\$\{\{\s*matrix\.([\w-]+)\s*\}\}$/)?.[1])
      .filter((k): k is string => k !== undefined),
  );
  const out: Found[] = [];
  const entry = (line: number, start: number, raw: string) => {
    const value = raw.replace(/^(['"])(.*)\1$/, '$2');
    if (value === '' || /\$\{\{/.test(value)) return;
    out.push(loc(sdk, file, 'matrix', line, start + (value === raw ? 0 : 1), value));
  };
  const lines = doc.lines;
  for (let i = 0; i < lines.length; i++) {
    const m = lines[i].text.match(/^(\s*)([\w-]+):(\s*)(\[.*\])?\s*(#.*)?$/);
    if (!m || !keys.has(m[2])) continue;
    if (m[4]) {
      // Inline: `node: [20, '22.x']`.
      let offset = m[1].length + m[2].length + 1 + m[3].length + 1;
      for (const part of m[4].slice(1, -1).split(',')) {
        const value = part.trim();
        if (value) entry(i, offset + part.indexOf(value), value);
        offset += part.length + 1;
      }
      continue;
    }
    // Block: `node:` then `- 20` lines indented deeper.
    for (let j = i + 1; j < lines.length; j++) {
      const b = lines[j].text.match(/^(\s*)-\s+([^\s#]+)\s*(#.*)?$/);
      if (!b || b[1].length <= m[1].length) break;
      entry(j, lines[j].text.indexOf(b[2]), b[2]);
    }
  }
  return out;
}

/**
 * `with:` keys of a setup-* step, found line-by-line: a `uses: actions/setup-x@` line, then the
 * following lines indented deeper than the step's `- `, up to the next step.
 */
function scanSetupAction(sdk: Sdk, file: string, doc: TextDoc): Found[] {
  const out: Found[] = [];
  const { action, keys } = SETUP_ACTIONS[sdk];
  const lines = doc.lines;
  for (let i = 0; i < lines.length; i++) {
    const m = lines[i].text.match(/^(\s*)(-\s+)?uses:\s*['"]?([^@\s'"]+)@/);
    if (!m || m[3].toLowerCase() !== action) continue;
    // Step indentation: the `- ` that opens this step (on this line or above).
    let stepIndent = m[2] ? m[1].length : -1;
    if (stepIndent < 0) {
      for (let j = i - 1; j >= 0; j--) {
        const d = lines[j].text.match(/^(\s*)-\s/);
        if (d) {
          stepIndent = d[1].length;
          break;
        }
      }
    }
    const step: Found[] = [];
    const keyIndents: number[] = [];
    let quality: { line: number; start: number; raw: string; indent: number } | null = null;
    for (let j = i + 1; j < lines.length; j++) {
      const t = lines[j].text;
      if (/^\s*$/.test(t) || /^\s*#/.test(t)) continue;
      const indent = t.match(/^(\s*)/)![1].length;
      if (indent <= stepIndent) break;
      if (/^\s*[A-Za-z0-9_-]+:/.test(t)) keyIndents.push(indent);
      for (const key of keys) {
        const km = t.match(new RegExp(`^(\\s*${key}:\\s*)(['"]?)([^'"#\\n]*?)\\2(\\s*(#.*)?)$`));
        if (!km) continue;
        const raw = km[3];
        const start = km[1].length + km[2].length;
        // platform-locations-PRD R7: a literal block (`|`, `|-`) lists one version per line, each its own location.
        if (/^\|[-+]?$/.test(raw.trim()) && !km[2]) {
          const entries: Found[] = [];
          for (let k = j + 1; k < lines.length; k++) {
            const e = lines[k].text.match(/^(\s*)(['"]?)([^\s'"#]+)\2\s*(#.*)?$/);
            if (!e || e[1].length <= indent) break;
            const computed = /\$\{\{/.test(e[3]);
            entries.push(loc(sdk, file, 'setup-action', k, e[1].length + e[2].length, e[3], { computed, listed: true, reason: computed ? 'expression' : null }));
          }
          if (entries.length > 0) {
            step.push(...entries);
            continue;
          }
        }
        const computed = raw === '' || /^[|>][-+]?$/.test(raw.trim()) || /\$\{\{/.test(raw);
        step.push(loc(sdk, file, 'setup-action', j, start, raw, { computed, reason: computed ? 'expression or multi-line value' : null }));
      }
      const qm = sdk === 'dotnet' ? t.match(/^(\s*dotnet-quality:\s*)(['"]?)([^'"#\s]*)\2(\s*(#.*)?)$/) : null;
      if (qm) quality = { line: j, start: qm[1].length + qm[2].length, raw: qm[3], indent };
    }
    out.push(...step);
    if (quality) {
      // #617 D31: the quality applies to the step's version; removing it must leave the `with:` mapping non-empty.
      const version = step.find((f) => !f.computed);
      const alone = keyIndents.filter((n) => n === quality!.indent).length <= 1;
      const computed = alone || quality.raw === '' || /\$\{\{/.test(quality.raw);
      out.push(loc(sdk, file, 'dotnet-quality', quality.line, quality.start, quality.raw, {
        channel: version?.channel ?? null,
        computed,
        reason: alone ? "the step's only with: input" : computed ? 'expression or empty value' : null,
      }));
    }
  }
  return out;
}

function scanVersionFile(sdk: Sdk, file: string, doc: TextDoc): Found[] {
  const first = doc.lines.findIndex((l) => l.text.trim() !== '' && !l.text.trim().startsWith('#'));
  if (first < 0) return [];
  const text = doc.lines[first].text;
  const value = text.trim();
  const start = text.indexOf(value);
  const ch = channelOf(sdk, value);
  return [loc(sdk, file, 'version-file', first, start, value, { computed: ch === null, reason: ch === null ? 'alias (lts/*, system, ...)' : null })];
}

function scanToolVersions(sdk: Sdk, file: string, doc: TextDoc): Found[] {
  const out: Found[] = [];
  doc.lines.forEach((l, i) => {
    const m = l.text.match(/^(\s*)(\S+)(\s+)(\S+)/);
    if (!m || !TOOL_VERSIONS_NAMES[sdk].includes(m[2])) return;
    out.push(loc(sdk, file, 'tool-versions', i, m[1].length + m[2].length + m[3].length, m[4]));
  });
  return out;
}

function scanEngines(file: string, doc: TextDoc): Found[] {
  return scanJsonKey('node', file, doc, 'node', 'engines', /"engines"\s*:/).map((f) => {
    const simple = /^(>=|\^|~)?\s*v?\d+(\.x|\.\d+){0,2}$/.test(f.value);
    return simple ? { ...f, channel: channelOf('node', f.value.replace(/^(>=|\^|~)\s*/, '')) } : { ...f, computed: true, reason: 'range (reported, not edited)' };
  });
}

function scanTypesNode(file: string, doc: TextDoc): Found[] {
  return scanJsonKey('node', file, doc, '@types/node', 'types-node').map((f) => ({
    ...f,
    channel: channelOf('node', f.value.replace(/^[\^~>=<\s]+/, '')),
    computed: true,
    reason: 'a package: planned through the npm adapter as a locked package (R7)',
  }));
}

function scanGoMod(file: string, doc: TextDoc): Found[] {
  const out: Found[] = [];
  doc.lines.forEach((l, i) => {
    let m = l.text.match(/^(go\s+)(\d+\.\d+(?:\.\d+)?)\s*$/);
    if (m) out.push(loc('go', file, 'go.mod', i, m[1].length, m[2]));
    m = l.text.match(/^(toolchain\s+go)(\d+\.\d+(?:\.\d+)?\S*)\s*$/);
    if (m) out.push(loc('go', file, 'toolchain', i, m[1].length, m[2]));
  });
  return out;
}

function scanMavenRelease(file: string, doc: TextDoc): Found[] {
  const out: Found[] = [];
  doc.lines.forEach((l, i) => {
    const m = l.text.match(/<(maven\.compiler\.release|maven\.compiler\.source|maven\.compiler\.target|java\.version)>([^<]+)<\//);
    if (!m || m.index === undefined) return;
    out.push(loc('java', file, 'maven-release', i, m.index + m[1].length + 2, m[2]));
  });
  return out;
}

/**
 * Every `<prefix>"value"` / `<prefix>'value'` on a line of its own, where `prefix` is a line-start
 * pattern such as `requires-python\s*=\s*`. `opts` decides, per value, what the location reports.
 */
function scanQuoted(sdk: Sdk, file: string, doc: TextDoc, kind: PlatformLocationKind, prefix: RegExp, opts: (value: string) => Partial<Found> = () => ({})): Found[] {
  const re = new RegExp(`^(\\s*${prefix.source})(['"])([^'"]+)\\2`);
  const out: Found[] = [];
  doc.lines.forEach((l, i) => {
    const m = l.text.match(re);
    if (m) out.push(loc(sdk, file, kind, i, m[1].length + 1, m[3], opts(m[3])));
  });
  return out;
}

const RANGE_REASON = 'a range or minimum: widened by hand, never narrowed or raised (#606)';
const reported = (reason: string) => (): Partial<Found> => ({ channel: null, computed: true, reason });

function scanRequiresPython(file: string, doc: TextDoc): Found[] {
  return scanQuoted('python', file, doc, 'requires-python', /requires-python\s*=\s*/, reported('a range (reported, not edited)'));
}

/** composer.json: `require.php` is a range (reported); `config.platform.php` is the exact version composer resolves for (edited). */
function scanComposer(file: string, doc: TextDoc): Found[] {
  const require = scanJsonKey('php', file, doc, 'php', 'composer-require', /"require"\s*:/).map((f) => ({
    ...f,
    channel: null,
    computed: true,
    reason: RANGE_REASON,
  }));
  const platform = scanJsonKey('php', file, doc, 'php', 'composer-platform', /"platform"\s*:/);
  return [...require, ...platform];
}

/** Gemfile `ruby "3.4.7"` (exact: edited) or `ruby "~> 4.0.0"` (a range: reported); `ruby file: ...` names no version. */
function scanGemfile(file: string, doc: TextDoc): Found[] {
  return scanQuoted('ruby', file, doc, 'gemfile-ruby', /ruby\s*\(?\s*/, (v) => (/^\d+\.\d+(\.\d+)?$/.test(v) ? {} : reported(RANGE_REASON)()));
}

/** A gemspec's `required_ruby_version`: a gem's minimum, reported only. */
function scanGemspec(file: string, doc: TextDoc): Found[] {
  return scanQuoted('ruby', file, doc, 'gemspec-ruby', /\w+\.required_ruby_version\s*=\s*/, reported(RANGE_REASON));
}

/** rust-toolchain.toml `channel = "1.99.0"` (edited); a floating `stable`/`beta`/`nightly` names no channel to move. */
function scanRustToolchain(file: string, doc: TextDoc): Found[] {
  return scanQuoted('rust', file, doc, 'rust-toolchain', /channel\s*=\s*/, (v) =>
    channelOf('rust', v) === null ? reported('a floating channel (stable, beta, nightly): nothing to move')() : {},
  );
}

/** Cargo.toml `rust-version` (the MSRV): reported only, never raised (#606 D11). */
function scanRustVersion(file: string, doc: TextDoc): Found[] {
  return scanQuoted('rust', file, doc, 'rust-version', /rust-version\s*=\s*/, reported('the MSRV: widened only, never raised by an SDK move (#606 D11)'));
}

export async function scanLocations(files: RepoFiles, sdks: readonly Sdk[] = SDKS): Promise<Found[]> {
  const out: Found[] = [];
  const definitions = new Map<string, PropertyDefinition[]>();
  const all = await files.list();
  for (const file of all) {
    const name = basename(file);
    const wanted: Array<(doc: TextDoc) => Found[]> = [];
    for (const sdk of sdks) {
      if (sdk === 'dotnet') {
        if (/\.(cs|fs|vb)proj$/.test(name) || name === 'Directory.Build.props') {
          wanted.push((d) => scanTfm(file, d), (d) => scanPackageVersion(file, d));
        }
        // R4: a project template (`*.csproj.in`) names its TFM through a property; the chain below resolves it.
        if (/\.(cs|fs|vb)proj\.in$/.test(name)) wanted.push((d) => scanTfm(file, d));
        if (/\.(props|targets)$/.test(name)) {
          wanted.push((d) => {
            propertyDefinitions(file, d, definitions);
            return [];
          });
        }
        if (name === 'template.json' && /(^|\/)\.template\.config\/template\.json$/.test(file)) wanted.push((d) => scanTemplateJson(file, d));
        if (name === 'global.json') {
          wanted.push((d) => scanJsonKey('dotnet', file, d, 'version', 'global.json', /"sdk"\s*:/));
          // An Arcade-style `tools.dotnet` is the SDK the build scripts install: it moves with `sdk.version` (SpaServices
          // #121). Its `runtimes` entries ("dotnet/x64") never match the key.
          wanted.push((d) => scanJsonKey('dotnet', file, d, 'dotnet', 'global.json', /"tools"\s*:/));
        }
      }
      if (sdk === 'node' && (name === 'action.yml' || name === 'action.yaml')) wanted.push((d) => scanActionRuntime(file, d));
      if (sdk === 'node' && name === 'package.json') {
        wanted.push((d) => scanEngines(file, d), (d) => scanTypesNode(file, d));
      }
      if (sdk === 'python' && name === 'pyproject.toml') wanted.push((d) => scanRequiresPython(file, d));
      if (sdk === 'java' && name === 'pom.xml') wanted.push((d) => scanMavenRelease(file, d));
      if (sdk === 'go' && name === 'go.mod') wanted.push((d) => scanGoMod(file, d));
      if (sdk === 'php' && name === 'composer.json') wanted.push((d) => scanComposer(file, d));
      if (sdk === 'ruby' && (name === 'Gemfile' || name === 'gems.rb')) wanted.push((d) => scanGemfile(file, d));
      if (sdk === 'ruby' && name.endsWith('.gemspec')) wanted.push((d) => scanGemspec(file, d));
      if (sdk === 'rust' && name === 'rust-toolchain.toml') wanted.push((d) => scanRustToolchain(file, d));
      if (sdk === 'rust' && name === 'Cargo.toml') wanted.push((d) => scanRustVersion(file, d));
      if (VERSION_FILES[sdk].includes(name)) wanted.push((d) => scanVersionFile(sdk, file, d));
      if (name === '.tool-versions') wanted.push((d) => scanToolVersions(sdk, file, d));
      if (isDockerfile(file)) wanted.push((d) => scanDocker(sdk, file, d));
      if (isWorkflowFile(file)) wanted.push((d) => scanSetupAction(sdk, file, d), (d) => scanWorkflowImage(sdk, file, d), (d) => scanMatrix(sdk, file, d));
    }
    if (wanted.length === 0) continue;
    let doc: TextDoc;
    try {
      doc = parseText(await files.readBytes(file), file);
    } catch {
      continue;
    }
    for (const scan of wanted) out.push(...scan(doc));
  }
  return sdks.includes('dotnet') ? resolveTfmChains(out, definitions) : out;
}

/** One single-line property definition in a `*.props`/`*.targets` PropertyGroup: `<Name>value</Name>`. */
interface PropertyDefinition {
  file: string;
  line: number;
  /** Where the value starts on the line. */
  start: number;
  value: string;
  /** A Condition (or any attribute) on the element or its PropertyGroup: its value depends on evaluation. */
  conditioned: boolean;
}

/** Collects the property definitions of one file; plain text, no import evaluation (as #663's `propertyFiles`). */
function propertyDefinitions(file: string, doc: TextDoc, into: Map<string, PropertyDefinition[]>): void {
  let group: { conditioned: boolean } | null = null;
  doc.lines.forEach((l, i) => {
    const open = l.text.match(/<PropertyGroup\b([^>]*?)(\/)?>/);
    if (open && !open[2]) group = { conditioned: /\S/.test(open[1]) };
    const current: { conditioned: boolean } | null = group;
    if (/<\/PropertyGroup>/.test(l.text)) group = null;
    if (current === null) return;
    // One element per line, opened and closed on it; the open tag may share the line with <PropertyGroup>.
    const re = /<([A-Za-z_][\w.-]*)(\s[^>]*)?>([^<]*)<\/\1>/g;
    let m: RegExpExecArray | null;
    while ((m = re.exec(l.text)) !== null) {
      const [whole, name, attrs = '', value] = m;
      if (name === 'PropertyGroup') continue;
      const definition = {
        file,
        line: i,
        start: m.index + whole.length - name.length - 3 - value.length,
        value,
        conditioned: current.conditioned || /\S/.test(attrs),
      };
      into.set(name, [...(into.get(name) ?? []), definition]);
    }
  });
}

/** A piece of an evaluated property: literal text from one definition, at its offset in that definition's value. */
interface Segment {
  text: string;
  definition: PropertyDefinition;
  offset: number;
}

const MAX_CHAIN = 8;

/** `$(Name)` expanded through single, unconditioned definitions; a string is why it cannot be. */
function expand(name: string, definitions: Map<string, PropertyDefinition[]>, seen: readonly string[] = []): Segment[] | string {
  if (seen.includes(name)) return `$(${name}) is a cycle (${[...seen, name].join(' -> ')})`;
  if (seen.length >= MAX_CHAIN) return `the chain to $(${name}) is longer than ${MAX_CHAIN}`;
  const defs = definitions.get(name) ?? [];
  if (defs.length === 0) return `$(${name}) is not defined in a .props or .targets file`;
  if (defs.length > 1) return `$(${name}) is defined ${defs.length} times (${[...new Set(defs.map((d) => d.file))].join(', ')})`;
  const [definition] = defs;
  if (definition.conditioned) return `$(${name}) is conditioned in ${definition.file}`;
  const out: Segment[] = [];
  const re = /\$\(([A-Za-z_][\w.-]*)\)/g;
  let at = 0;
  let m: RegExpExecArray | null;
  while ((m = re.exec(definition.value)) !== null) {
    if (m.index > at) out.push({ text: definition.value.slice(at, m.index), definition, offset: at });
    const inner = expand(m[1], definitions, [...seen, name]);
    if (typeof inner === 'string') return inner;
    out.push(...inner);
    at = m.index + m[0].length;
  }
  if (at < definition.value.length) out.push({ text: definition.value.slice(at), definition, offset: at });
  if (out.some((s) => /\$[({]/.test(s.text))) return `$(${name}) uses an expression the bot does not evaluate`;
  return out;
}

/**
 * platform-locations-PRD R4 (§5.4): a TFM that is exactly `$(X)` or `${X}` is followed to X's single, unconditioned
 * definition in the repository's `*.props`/`*.targets`, recursively. The token that carries the major (`10` in
 * `<AspNetCoreMajorVersion>10</AspNetCoreMajorVersion>`, or `10.0` in `<NetCurrent>net10.0</NetCurrent>`) becomes one
 * `tfm-property` location, however many projects read it, and the TFMs it resolves leave the list. A chain that does not
 * resolve plainly keeps its TFM computed (reported), with the reason in it.
 */
function resolveTfmChains(found: Found[], definitions: Map<string, PropertyDefinition[]>): Found[] {
  const out: Found[] = [];
  const properties = new Map<string, Found>();
  for (const f of found) {
    const ref = f.kind === 'tfm' && f.computed && !/^conditioned/.test(f.reason ?? '') ? f.value.match(/^\$[({]([A-Za-z_][\w.-]*)[)}]$/) : null;
    if (!ref) {
      out.push(f);
      continue;
    }
    const segments = expand(ref[1], definitions);
    const why = typeof segments === 'string' ? segments : carrier(segments);
    if (typeof why === 'string') {
      out.push({ ...f, reason: `computed TFM (${f.value}): ${why}` });
      continue;
    }
    const key = `${why.file}:${why.line}:${why.start}`;
    if (!properties.has(key)) properties.set(key, { ...why, readers: [] });
    properties.get(key)!.readers!.push(f.file);
  }
  return [...out, ...properties.values()];
}

/** The location of the token carrying an evaluated TFM's major, or why there is no single one. */
function carrier(segments: Segment[]): Found | string {
  const text = segments.map((s) => s.text).join('');
  const tfm = text.match(/^net(\d+)\.(\d+)$/);
  if (!tfm) return `evaluates to '${text}', not a netN.N TFM`;
  const [, major, minor] = tfm;
  const from = 3;
  let pos = 0;
  for (const s of segments) {
    const end = pos + s.text.length;
    if (pos <= from && end >= from + major.length) {
      const local = from - pos;
      const withMinor = s.text.slice(local).startsWith(`${major}.${minor}`);
      const value = withMinor ? `${major}.${minor}` : major;
      const d = s.definition;
      return loc('dotnet', d.file, 'tfm-property', d.line, d.start + s.offset + local, value, { channel: `${major}.${minor}`, computed: false, reason: null });
    }
    pos = end;
  }
  return `the major of '${text}' is spread over several properties`;
}

/** The public projection of a Found (what goes into a platform record). */
export function toLocation(f: Found): PlatformLocation {
  return { file: f.file, kind: f.kind, value: f.value, channel: f.channel, computed: f.computed, reason: f.reason };
}
