import { setupTestBed } from '@analogjs/vitest-angular/setup-testbed';

setupTestBed({ zoneless: true });

// Swallow jsdom's "Could not parse CSS stylesheet" parser noise (written straight
// to stderr by jsdom's VirtualConsole), letting every other write through.
const originalStderrWrite = process.stderr.write.bind(process.stderr);
process.stderr.write = ((chunk: string | Uint8Array, ...rest: unknown[]): boolean => {
  if (typeof chunk === 'string' && chunk.includes('Could not parse CSS stylesheet')) return true;
  return (originalStderrWrite as (...a: unknown[]) => boolean)(chunk, ...rest);
}) as typeof process.stderr.write;
