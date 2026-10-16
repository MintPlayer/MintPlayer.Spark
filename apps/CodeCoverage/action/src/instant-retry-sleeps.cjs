// Preloaded (`node --require`) into the bundle by bundle.test.ts, never shipped:
// the upload retry schedule sleeps whole seconds, 5 s and up, about 2 minutes in
// all, which a smoke test against a dead port must not spend in real time. Those
// sleeps run at once; shorter timers (undici's 500 ms timer ticks among them) are
// left alone.
'use strict';

const original = globalThis.setTimeout;
globalThis.setTimeout = function (callback, ms, ...args) {
  const isRetrySleep = typeof ms === 'number' && ms >= 5000 && ms % 1000 === 0;
  return original(callback, isRetrySleep ? 0 : ms, ...args);
};
