import { promises as fs } from 'node:fs';
import * as path from 'node:path';

/** Forward-slash, repo-relative paths. */
export interface RepoFiles {
  root: string;
  list(): Promise<string[]>;
  read(rel: string): Promise<string>;
  readBytes(rel: string): Promise<Buffer>;
  write(rel: string, bytes: Buffer): Promise<void>;
  exists(rel: string): Promise<boolean>;
}

const SKIP_DIRS = new Set(['.git', 'node_modules', 'bin', 'obj', 'target', 'vendor', '.venv', 'dist', '.nx', '.angular', '.gradle']);

async function walk(root: string, rel: string, out: string[]): Promise<void> {
  const entries = await fs.readdir(path.join(root, rel), { withFileTypes: true });
  for (const e of entries) {
    const child = rel ? `${rel}/${e.name}` : e.name;
    if (e.isDirectory()) {
      if (SKIP_DIRS.has(e.name)) continue;
      await walk(root, child, out);
    } else if (e.isFile()) {
      out.push(child);
    }
  }
}

/** A directory tree. `.github` is walked (setup-* actions live there); build output is skipped. */
export function directoryFiles(root: string): RepoFiles {
  let cache: string[] | undefined;
  const abs = (rel: string) => {
    const p = path.resolve(root, rel);
    const rootAbs = path.resolve(root);
    if (p !== rootAbs && !p.startsWith(rootAbs + path.sep)) throw new Error(`path escapes the repository: ${rel}`);
    return p;
  };
  return {
    root,
    async list() {
      if (!cache) {
        cache = [];
        await walk(root, '', cache);
        cache.sort();
      }
      return cache;
    },
    read: async (rel) => fs.readFile(abs(rel), 'utf8'),
    readBytes: async (rel) => fs.readFile(abs(rel)),
    async write(rel, bytes) {
      await fs.mkdir(path.dirname(abs(rel)), { recursive: true });
      await fs.writeFile(abs(rel), bytes);
      cache = undefined;
    },
    exists: async (rel) =>
      fs
        .stat(abs(rel))
        .then((s) => s.isFile())
        .catch(() => false),
  };
}

export function basename(rel: string): string {
  const i = rel.lastIndexOf('/');
  return i < 0 ? rel : rel.slice(i + 1);
}

export function dirname(rel: string): string {
  const i = rel.lastIndexOf('/');
  return i < 0 ? '.' : rel.slice(0, i);
}

export function joinRel(dir: string, file: string): string {
  return dir === '.' || dir === '' ? file : `${dir}/${file}`;
}

export function isWorkflowFile(rel: string): boolean {
  return /^\.github\/workflows\/[^/]+\.ya?ml$/.test(rel) || /(^|\/)action\.ya?ml$/.test(rel);
}

export function isDockerfile(rel: string): boolean {
  const b = basename(rel);
  return /^Dockerfile(\..+)?$/.test(b) || /\.Dockerfile$/i.test(b) || /^Containerfile$/.test(b);
}
