// @vitest-environment node

import { resolve } from 'node:path';
import { afterEach, beforeEach, expect, test, vi } from 'vitest';

const mocks = vi.hoisted(() => ({
  build: vi.fn(), preview: vi.fn(), launch: vi.fn(), mkdir: vi.fn(), writeSync: vi.fn(),
}));
vi.mock('vite', () => ({ build: mocks.build, preview: mocks.preview }));
vi.mock('@playwright/test', () => ({ chromium: { launch: mocks.launch } }));
vi.mock('node:fs/promises', async importOriginal => ({ ...await importOriginal(), mkdir: mocks.mkdir }));
vi.mock('node:fs', async importOriginal => ({ ...await importOriginal(), writeSync: mocks.writeSync }));

function deferred() {
  let resolvePromise, reject;
  const promise = new Promise((resolve, fail) => { resolvePromise = resolve; reject = fail; });
  return { promise, resolve: resolvePromise, reject };
}
const entry = { output: [{ type: 'chunk', isEntry: true, code: 'owned entry' }] };
const run = () => import('./check-restored-live-browser.mjs');

beforeEach(() => {
  vi.resetModules();
  vi.resetAllMocks();
  vi.stubEnv('RESTORED_SMOKE_API', 'http://127.0.0.1:41234');
  vi.stubEnv('RESTORED_SMOKE_INSTANCE', 'a'.repeat(32));
  vi.stubEnv('RESTORED_SMOKE_ROOT', resolve('owned-restored-preparation-test'));
  vi.stubEnv('RESTORED_SMOKE_PORT', '41235');
  vi.stubEnv('PAYPLANNER_TEST_BROWSER', 'chrome');
  for (const name of ['VITE_RESTORED_RUNTIME_ISOLATION', 'VITE_RESTORED_RUNTIME_INSTANCE_ID', 'VITE_API_URL'])
    vi.stubEnv(name, undefined);
  vi.spyOn(console, 'log').mockImplementation(() => {});
  mocks.mkdir.mockResolvedValue(undefined);
});
afterEach(() => {
  vi.unstubAllEnvs();
  vi.restoreAllMocks();
});

test('starts the configured browser while the real UI preparation is still pending and closes it after a build failure', async () => {
  const compilation = deferred();
  const started = deferred();
  const browser = { close: vi.fn().mockResolvedValue(undefined) };
  const failure = new Error('owned build failure');
  mocks.build.mockImplementation(() => { started.resolve(); return compilation.promise; });
  mocks.launch.mockResolvedValue(browser);
  const result = run().catch(error => error);
  await started.promise;
  expect(mocks.build).toHaveBeenCalledTimes(1);
  expect(mocks.launch).toHaveBeenCalledWith({ headless: true, channel: 'chrome' });
  expect(mocks.preview).not.toHaveBeenCalled();
  compilation.reject(failure);
  expect(await result).toBe(failure);
  expect(browser.close).toHaveBeenCalledTimes(1);
  expect(mocks.preview).not.toHaveBeenCalled();
});

test('waits for browser ownership before preview and closes it when preview fails', async () => {
  const launch = deferred();
  const started = deferred();
  const browser = { close: vi.fn().mockResolvedValue(undefined) };
  const failure = new Error('owned preview failure');
  mocks.build.mockImplementation(() => { started.resolve(); return Promise.resolve(entry); });
  mocks.launch.mockReturnValue(launch.promise);
  mocks.preview.mockRejectedValue(failure);
  const result = run().catch(error => error);
  await started.promise;
  expect(mocks.build).toHaveBeenCalledTimes(1);
  expect(mocks.preview).not.toHaveBeenCalled();
  launch.resolve(browser);
  expect(await result).toBe(failure);
  expect(browser.close).toHaveBeenCalledTimes(1);
});

test('fails a missing configured browser without silently trying another channel or starting preview', async () => {
  mocks.build.mockResolvedValue(entry);
  mocks.launch.mockRejectedValue(new Error('configured browser missing'));
  await expect(run()).rejects.toThrow('An installed Chromium-compatible browser is required');
  expect(mocks.launch).toHaveBeenCalledTimes(1);
  expect(mocks.launch).toHaveBeenCalledWith({ headless: true, channel: 'chrome' });
  expect(mocks.preview).not.toHaveBeenCalled();
});

test('rejects an invalid browser configuration before compiling or creating output', async () => {
  vi.stubEnv('PAYPLANNER_TEST_BROWSER', 'unexpected');
  await expect(run()).rejects.toThrow('Use the configured installed Edge or Chrome channel');
  expect(mocks.build).not.toHaveBeenCalled();
  expect(mocks.launch).not.toHaveBeenCalled();
  expect(mocks.mkdir).not.toHaveBeenCalled();
});
