// @vitest-environment node

import { spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { discoverIntegrationTests, selectIntegrationTests } from './run-integration-tests.mjs';

const runner = resolve('scripts/run-integration-tests.mjs');
const roots: string[] = [];
const catalog = ['src/nested/alpha.integration.test.ts', 'vite.runtime.integration.test.ts'];

function fixture(exitCode = 0) {
  const root = mkdtempSync(join(tmpdir(), 'pp-integration-runner-'));
  roots.push(root);
  for (const file of [...catalog, 'src/ordinary.test.ts', 'other/ignored.integration.test.ts']) {
    mkdirSync(dirname(join(root, file)), { recursive: true });
    writeFileSync(join(root, file), '');
  }
  const child = join(root, 'node_modules/vitest/vitest.mjs');
  mkdirSync(dirname(child), { recursive: true });
  writeFileSync(child, `import { writeFileSync } from 'node:fs';
writeFileSync('invocation.json', JSON.stringify({ args: process.argv.slice(2), cwd: process.cwd() }));
process.exitCode = ${exitCode};
`);
  return root;
}

function run(root: string, args: string[] = []) {
  return spawnSync(process.execPath, [runner, ...args], {
    cwd: root, encoding: 'utf8', shell: false,
  });
}

afterEach(() => {
  for (const root of roots.splice(0)) rmSync(root, { recursive: true, force: true });
});

describe('integration runner', () => {
  it('runs the entire src and root catalog by default with one worker', () => {
    const root = fixture();
    expect(run(root).status).toBe(0);
    expect(JSON.parse(readFileSync(join(root, 'invocation.json'), 'utf8'))).toEqual({
      args: ['run', ...catalog, '--maxWorkers=1'], cwd: root,
    });
  });

  it('keeps every current integration file in default discovery', () => {
    expect(discoverIntegrationTests(process.cwd())).toEqual([
      'src/components/Accounts/Accounts.integration.test.tsx',
      'src/components/Accounts/InstallmentPlanner.integration.test.tsx',
      'src/components/Accounts/InvoiceModal.integration.test.tsx',
      'src/components/Acts/ActModal.integration.test.tsx',
      'src/components/Calendar/Calendar.integration.test.tsx',
      'src/components/Calendar/PaymentModal.integration.test.tsx',
      'src/components/Clients/ClientAddressEntriesForm.integration.test.tsx',
      'src/components/Clients/ClientDetail.integration.test.tsx',
      'src/components/Clients/ClientPassportEntriesForm.integration.test.tsx',
      'src/components/Contracts/ContractDetailPage.integration.test.tsx',
      'src/components/Contracts/ContractModal.integration.test.tsx',
      'src/components/LegalEntities/LegalEntityModal.integration.test.tsx',
      'src/components/Money/MoneyOperations.integration.test.tsx',
      'src/components/Payments/Payments.integration.test.tsx',
      'src/components/Tasks/useTaskBulkBilling.integration.test.tsx',
      'vite.restored-browser.integration.test.ts',
      'vite.restored-runtime.integration.test.ts',
    ]);
  });

  it('forwards only selected files and supported reporter arguments to the real child boundary', () => {
    const root = fixture();
    expect(run(root, [catalog[1], '--reporter', 'verbose', '--reporter=json']).status).toBe(0);
    expect(JSON.parse(readFileSync(join(root, 'invocation.json'), 'utf8')).args).toEqual([
      'run', catalog[1], '--maxWorkers=1', '--reporter=verbose', '--reporter=json',
    ]);
  });

  it('normalizes relative Windows paths and deduplicates explicit selections', () => {
    expect(selectIntegrationTests(['./vite.runtime.integration.test.ts', 'src\\nested\\alpha.integration.test.ts', catalog[1]], catalog)).toEqual({
      files: catalog, options: [],
    });
  });

  it.each(['missing.integration.test.ts', 'src/ordinary.test.ts', 'src/nested', '*.integration.test.ts', '', '../src/nested/alpha.integration.test.ts'])('rejects invalid selection %j before starting Vitest', (selector) => {
    const root = fixture();
    const result = run(root, [selector]);
    expect(result.status).toBe(1);
    expect(result.stderr).toContain('Unknown integration file');
    expect(() => readFileSync(join(root, 'invocation.json'))).toThrow();
  });

  it.each(['--passWithNoTests', '--exclude=src/**', '--coverage=false', '--maxWorkers=8', '--testNamePattern=missing', '-t', '--watch'])('rejects unsupported option %s before starting Vitest', (option) => {
    const root = fixture();
    expect(run(root, [catalog[0], option]).status).toBe(1);
    expect(() => readFileSync(join(root, 'invocation.json'))).toThrow();
  });

  it.each([['--reporter'], ['--reporter=unknown'], ['--reporter', '--passWithNoTests']])('rejects invalid reporter %j', (...args) => {
    expect(() => selectIntegrationTests(args, catalog)).toThrow('supported --reporter');
  });

  it('fails closed on substring collisions rather than silently including another integration file', () => {
    const files = ['src/a.integration.test.ts', 'src/a.integration.test.tsx'];
    expect(() => selectIntegrationTests([files[0]], files)).toThrow('Ambiguous Vitest file filter');
    expect(selectIntegrationTests(files, files).files).toEqual(files);
  });

  it('shows help without requiring a catalog or starting Vitest', () => {
    const root = fixture();
    rmSync(join(root, 'src'), { recursive: true });
    const result = run(root, ['--help']);
    expect(result.status).toBe(0);
    expect(result.stdout).toContain('exact repository-relative paths');
    expect(() => readFileSync(join(root, 'invocation.json'))).toThrow();
  });

  it('rejects an empty catalog', () => {
    expect(() => selectIntegrationTests([], [])).toThrow('No integration tests found');
  });

  it('preserves a failed Vitest exit code', () => {
    expect(run(fixture(7)).status).toBe(7);
  });

  it('fails when the child cannot load instead of reporting a successful run', () => {
    const root = fixture();
    rmSync(join(root, 'node_modules/vitest/vitest.mjs'));
    expect(run(root).status).not.toBe(0);
  });
});
