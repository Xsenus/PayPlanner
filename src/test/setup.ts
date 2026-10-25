// Node suites deliberately do not load React Testing Library or browser shims.
const hasDom = typeof window !== 'undefined' && typeof document !== 'undefined';

if (hasDom) {
  await import('./setup.dom');
}

export {};
