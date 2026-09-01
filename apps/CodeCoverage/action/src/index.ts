import { run } from './main';

// Entry point only. The logic lives in main.ts so the tests can import it without the
// action executing on import, which is the shape the other actions here use.
run();
