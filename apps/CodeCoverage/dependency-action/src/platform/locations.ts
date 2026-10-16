import type { PlatformLocation, PlatformLocationKind, Sdk } from '../contracts/types';
import { parseText, type TextDoc } from '../core/lineEdit';
import { basename, isDockerfile, isWorkflowFile, type RepoFiles } from '../util/repoFiles';
import { channelOf, tfmChannel } from './channels';

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
}

const DOCKER_IMAGES: Record<Sdk, RegExp> = {
  dotnet: /^(?:mcr\.microsoft\.com\/dotnet\/(?:nightly\/)?[\w.-]+)$/,
  node: /^(?:docker\.io\/)?(?:library\/)?node$/,
  python: /^(?:docker\.io\/)?(?:library\/)?python$/,
  java: /^(?:docker\.io\/)?(?:library\/)?(?:eclipse-temurin|openjdk|amazoncorretto|ibm-semeru-runtimes)$/,
  go: /^(?:docker\.io\/)?(?:library\/)?golang$/,
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
};

const VERSION_FILES: Record<Sdk, string[]> = {
  dotnet: [],
  node: ['.nvmrc', '.node-version'],
  python: ['.python-version'],
  java: ['.java-version'],
  go: ['.go-version'],
};

const TOOL_VERSIONS_NAMES: Record<Sdk, string[]> = {
  dotnet: ['dotnet', 'dotnet-core'],
  node: ['nodejs', 'node'],
  python: ['python'],
  java: ['java'],
  go: ['golang', 'go'],
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
      const computed = conditioned || /\$\(/.test(value);
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
  });
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
    const m = l.text.match(/<(Version|VersionPrefix)>([^<]+)<\/(Version|VersionPrefix)>/);
    if (!m || m.index === undefined) return;
    const value = m[2];
    const start = m.index + m[1].length + 2;
    const major = value.match(/^(\d+)\./);
    out.push(loc('dotnet', file, 'package-version', i, start, value, { channel: major ? `${major[1]}.0` : null }));
  });
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
  doc.lines.forEach((l, i) => {
    const m = l.text.match(/^(\s*FROM\s+(?:--platform=\S+\s+)?)([^\s:@]+):([^\s@]+)(@sha256:[0-9a-f]{64})?/i);
    if (!m) return;
    if (!DOCKER_IMAGES[sdk].test(m[2])) return;
    const start = m[1].length + m[2].length + 1;
    const digest = !!m[4];
    const computed = digest || /\$/.test(m[3]);
    out.push(
      loc(sdk, file, 'docker', i, start, m[3], {
        computed,
        reason: digest ? 'digest-pinned image (the bot plans image digests as docker records)' : computed ? 'computed tag (ARG)' : null,
      }),
    );
  });
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
    for (let j = i + 1; j < lines.length; j++) {
      const t = lines[j].text;
      if (/^\s*$/.test(t) || /^\s*#/.test(t)) continue;
      const indent = t.match(/^(\s*)/)![1].length;
      if (indent <= stepIndent) break;
      for (const key of keys) {
        const km = t.match(new RegExp(`^(\\s*${key}:\\s*)(['"]?)([^'"#\\n]*?)\\2(\\s*(#.*)?)$`));
        if (!km) continue;
        const raw = km[3];
        const start = km[1].length + km[2].length;
        const computed = raw === '' || raw === '|' || raw === '>' || /\$\{\{/.test(raw);
        out.push(loc(sdk, file, 'setup-action', j, start, raw, { computed, reason: computed ? 'expression or multi-line value' : null }));
      }
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

function scanRequiresPython(file: string, doc: TextDoc): Found[] {
  const out: Found[] = [];
  doc.lines.forEach((l, i) => {
    const m = l.text.match(/^(\s*requires-python\s*=\s*")([^"]+)"/);
    if (!m) return;
    out.push(loc('python', file, 'requires-python', i, m[1].length, m[2], { channel: null, computed: true, reason: 'a range (reported, not edited)' }));
  });
  return out;
}

export async function scanLocations(files: RepoFiles, sdks: readonly Sdk[] = ['dotnet', 'node', 'python', 'java', 'go']): Promise<Found[]> {
  const out: Found[] = [];
  const all = await files.list();
  for (const file of all) {
    const name = basename(file);
    const wanted: Array<(doc: TextDoc) => Found[]> = [];
    for (const sdk of sdks) {
      if (sdk === 'dotnet') {
        if (/\.(cs|fs|vb)proj$/.test(name) || name === 'Directory.Build.props') {
          wanted.push((d) => scanTfm(file, d), (d) => scanPackageVersion(file, d));
        }
        if (name === 'global.json') wanted.push((d) => scanJsonKey('dotnet', file, d, 'version', 'global.json', /"sdk"\s*:/));
      }
      if (sdk === 'node' && name === 'package.json') {
        wanted.push((d) => scanEngines(file, d), (d) => scanTypesNode(file, d));
      }
      if (sdk === 'python' && name === 'pyproject.toml') wanted.push((d) => scanRequiresPython(file, d));
      if (sdk === 'java' && name === 'pom.xml') wanted.push((d) => scanMavenRelease(file, d));
      if (sdk === 'go' && name === 'go.mod') wanted.push((d) => scanGoMod(file, d));
      if (VERSION_FILES[sdk].includes(name)) wanted.push((d) => scanVersionFile(sdk, file, d));
      if (name === '.tool-versions') wanted.push((d) => scanToolVersions(sdk, file, d));
      if (isDockerfile(file)) wanted.push((d) => scanDocker(sdk, file, d));
      if (isWorkflowFile(file)) wanted.push((d) => scanSetupAction(sdk, file, d));
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
  return out;
}

/** The public projection of a Found (what goes into a platform record). */
export function toLocation(f: Found): PlatformLocation {
  return { file: f.file, kind: f.kind, value: f.value, channel: f.channel, computed: f.computed, reason: f.reason };
}
